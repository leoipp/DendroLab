"""Train models through IFPC and package them for DendroLab's Processador ZIP reader."""

from __future__ import annotations

import json
import os
import pickle
import re
import sys
import traceback
import zipfile
import tempfile
from datetime import datetime
from pathlib import Path

import numpy as np
import pandas as pd


def _json_value(value):
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_value(item) for item in value]
    if isinstance(value, np.ndarray):
        return [_json_value(item) for item in value.tolist()]
    if isinstance(value, np.generic):
        return _json_value(value.item())
    if isinstance(value, float) and not np.isfinite(value):
        return None
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    return str(value)


def _safe_name(value):
    cleaned = re.sub(r"[^\w.-]+", "_", str(value), flags=re.UNICODE).strip("._ ")
    return cleaned[:90] or "modelo"


def _tree_payload(tree):
    state = tree.tree_
    return {
        "left": state.children_left.tolist(),
        "right": state.children_right.tolist(),
        "feature": state.feature.tolist(),
        "threshold": state.threshold.tolist(),
        "value": state.value[:, 0, 0].tolist(),
    }


def _estimator_payload(model, model_type):
    if model_type == "random_forest":
        return {"kind": model_type, "trees": [_tree_payload(item) for item in model.estimators_]}
    if model_type == "gradient_boosting":
        return {
            "kind": model_type,
            "initial": float(np.asarray(model.init_.constant_).reshape(-1)[0]),
            "learning_rate": float(model.learning_rate),
            "trees": [_tree_payload(item[0]) for item in model.estimators_],
        }
    if model_type == "svr":
        if model.kernel not in ("linear", "rbf", "poly", "sigmoid"):
            raise ValueError(f"Kernel SVR não suportado no Processador: {model.kernel}")
        return {
            "kind": model_type,
            "kernel": model.kernel,
            "support_vectors": model.support_vectors_.tolist(),
            "dual_coef": model.dual_coef_[0].tolist(),
            "intercept": float(model.intercept_[0]),
            "gamma": float(model._gamma),
            "coef0": float(model.coef0),
            "degree": int(model.degree),
        }
    raise ValueError(f"Tipo de modelo não suportado para saída do Processador: {model_type}")


def _load_fitter(ifpc_root):
    root = Path(ifpc_root).resolve()
    cli = root / "cli"
    if not (cli / "ModelFitter.py").is_file():
        raise FileNotFoundError(f"ModelFitter.py não encontrado em {cli}")
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(cli))
    os.chdir(root)
    import ModelFitter as fitter
    return fitter


def _read_table(path):
    return pd.read_csv(path, sep=";", encoding="utf-8-sig", low_memory=False)


def train(request, fitter):
    data = _read_table(request["input_csv"])
    target = request["target"]
    features = request["features"]
    stratifiers = request.get("stratifiers", [])
    config = request["config"]
    missing = [column for column in [target, *features, *stratifiers] if column not in data.columns]
    if missing:
        raise ValueError("Colunas ausentes na tabela: " + ", ".join(missing))
    if not features:
        raise ValueError("Selecione ao menos uma variável explicativa.")
    if target in features:
        raise ValueError("A variável resposta Y não pode ser usada como preditora.")

    if stratifiers:
        grouped = data.groupby(stratifiers, dropna=False, sort=False)
        strata = []
        for key, frame in grouped:
            labels = key if isinstance(key, tuple) else (key,)
            label = "_".join("vazio" if pd.isna(part) else str(part) for part in labels)
            strata.append((label, frame.copy()))
    else:
        strata = [("geral", data)]

    outputs = []
    output_dir = Path(request["output_directory"]).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    for stratum_name, frame in strata:
        model, scalers, metrics, encoder_info, history, (observed, estimated) = fitter.treinar_modelo(
            frame, target, features, config
        )
        if model is None:
            raise ValueError(f"Treinamento sem modelo no estrato '{stratum_name}'. Verifique a quantidade de linhas completas.")
        metadata = {
            "model_type": config.get("model_type", "neural_net"),
            "target": target,
            "features": features,
            "estrato": stratum_name,
            "config": config,
            "metricas": metrics,
            "n_samples": int(len(frame)),
            "timestamp": datetime.now().isoformat(),
            "cols_categoricas": encoder_info["cols_categoricas"] if encoder_info else [],
            "cols_numericas": encoder_info["cols_numericas"] if encoder_info else features,
            "format_version": 1,
        }
        base_name = _safe_name(f"{target}_{stratum_name}")
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        bundle_prefix = f"{base_name}_{stamp}"
        bundle_path = output_dir / f"{bundle_prefix}.zip"
        with zipfile.ZipFile(bundle_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            bundle.writestr(f"{bundle_prefix}_metadata.json", json.dumps(_json_value(metadata), ensure_ascii=False, indent=2))
            bundle.writestr(f"{bundle_prefix}_scalers.pkl", pickle.dumps(scalers, protocol=pickle.HIGHEST_PROTOCOL))
            if encoder_info is not None:
                bundle.writestr(f"{bundle_prefix}_encoder.pkl", pickle.dumps(encoder_info, protocol=pickle.HIGHEST_PROTOCOL))
            if config.get("model_type", "neural_net") == "neural_net":
                with tempfile.TemporaryDirectory(prefix="dendrolab_model_export_") as temporary:
                    keras_path = Path(temporary) / f"{bundle_prefix}_model.keras"
                    model.save(keras_path)
                    bundle.write(keras_path, arcname=f"{bundle_prefix}_model.keras")
            else:
                payload = _estimator_payload(model, config["model_type"])
                bundle.writestr(f"{bundle_prefix}_model.json", json.dumps(_json_value(payload), separators=(",", ":")))
        outputs.append({"bundle": str(bundle_path), "metadata": metadata})

    return {
        "ok": True,
        "mode": "train",
        "models": _json_value(outputs),
        "output_directory": str(output_dir),
    }


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Uso: model_fitter_bridge.py <ifpc_root> <request.json>")
    root, request_path = sys.argv[1:]
    request = json.loads(Path(request_path).read_text(encoding="utf-8"))
    try:
        fitter = _load_fitter(root)
        result = train(request, fitter)
        Path(request["response_json"]).write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    except BaseException as error:
        failure = {"ok": False, "error": str(error), "traceback": traceback.format_exc()}
        Path(request["response_json"]).write_text(json.dumps(failure, ensure_ascii=False), encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
