using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Tabular;

namespace Dendro.Core.Regression;

public sealed record TaperModel(string Id, string Name, string Formula, string[] Parameters, double[] Initial);
public sealed record TaperOptions(string Tree, string Dap, string TotalHeight, string Height, string Diameter, string Stratum, string[] Models);
public sealed record TaperResult(TableResult[] Tables, JsonArray Fits, int Rows, int Invalid);

public static class TaperAdjustment
{
    private static readonly ConcurrentDictionary<string, Formula> Compiled = new(StringComparer.Ordinal);
    public static readonly TaperModel[] Models = [
        new("kozak_model", "Kozak", "DAP*sqrt(max(b0+b1*z+b2*z^2,0))", ["b0","b1","b2"], [1,-.1,-.5]),
        new("demaerschalk_model", "Demaerschalk", "10^b0*DAP^b1*H^(2*b2)*(H-h)^(2*b3)", ["b0","b1","b2","b3"], [-1,1,0,.5]),
        new("ormerod_diameter_model", "Ormerod", "DAP*((H-h)/(H-1.3))^b1", ["b1"], [.6]),
        new("garcia_model", "Garcia", "DAP*sqrt(max(b0+b1*sqrt(z)+b2*z,0))", ["b0","b1","b2"], [1,-.1,-.5]),
        new("garay_model", "Garay", "DAP^b0*(1+b1*ln(1-b2*z^b3))", ["b0","b1","b2","b3"], [1,-.5,.9,1])
    ];
    public static JsonArray DefaultDefinitions() => new(Models.Select(model => (JsonNode?)new JsonObject
    {
        ["id"] = model.Id, ["name"] = model.Name, ["formula"] = model.Formula,
        ["parameters"] = new JsonArray(model.Parameters.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["initial"] = new JsonArray(model.Initial.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
    }).ToArray());

    public static TaperModel[] ReadModels(JsonObject project)
    {
        var entries = project.Obj("taper").Arr("equations");
        if (entries.Count == 0) return Models;
        return entries.OfType<JsonObject>().Select(entry => new TaperModel(entry.S("id"), entry.S("name"), entry.S("formula"),
            entry.Arr("parameters").Select(p => p?.GetValue<string>() ?? "").ToArray(),
            entry.Arr("initial").Select(p => p?.GetValue<double>() ?? 1).ToArray())).ToArray();
    }

    public static double Predict(TaperModel model, double[] b, double dap, double total, double height)
    {
        if (!double.IsFinite(dap) || !double.IsFinite(total) || !double.IsFinite(height) || dap <= 0 || total <= 1.3 || height < 0 || height > total) return double.NaN;
        try
        {
            var formula = Compiled.GetOrAdd(model.Id + "\0" + model.Formula, _ => new Formula(model.Formula, model.Parameters.Concat(["DAP", "H", "h", "z"])));
            return formula.Evaluate(name => name switch
            {
                "DAP" => dap, "H" => total, "h" => height, "z" => height / total,
                _ => b[Array.IndexOf(model.Parameters, name)],
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or ArithmeticException or IndexOutOfRangeException) { return double.NaN; }
    }
    public static double Predict(string model, double[] b, double dap, double total, double height)
    {
        if (!double.IsFinite(dap) || !double.IsFinite(total) || !double.IsFinite(height) || dap <= 0 || total <= 1.3 || height < 0 || height > total) return double.NaN;
        double z = height / total;
        return model switch {
            "kozak_model" => dap * Math.Sqrt(Math.Max(b[0] + b[1]*z + b[2]*z*z, 0)),
            "demaerschalk_model" => Math.Pow(10,b[0])*Math.Pow(dap,b[1])*Math.Pow(total,2*b[2])*Math.Pow(total-height,2*b[3]),
            "ormerod_diameter_model" => dap*Math.Pow((total-height)/(total-1.3),b[0]),
            "garcia_model" => dap*Math.Sqrt(Math.Max(b[0]+b[1]*Math.Sqrt(z)+b[2]*z,0)),
            "garay_model" => Math.Pow(dap,b[0])*(1+b[1]*Math.Log(1-b[2]*Math.Pow(z,b[3]))),
            _ => throw new ArgumentException("Modelo de afilamento desconhecido.")
        };
    }
    private sealed record Row(object?[] Values, string Tree, string Group, double Dap, double H, double h, double d)
    {
        public bool Valid => Tree.Length > 0 && double.IsFinite(Dap) && double.IsFinite(H) && double.IsFinite(h) && double.IsFinite(d)
            && Dap > 0 && H > 1.3 && h >= 0 && h < H && d > 0;
    }
    public static TaperResult Run(TableSource source, TaperOptions options, string output, CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
        => Run(source, options, output, Models, cancellation, progress);

    public static TaperResult Run(TableSource source, TaperOptions options, string output, TaperModel[] models, CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        var selected = models.Where(m => options.Models.Contains(m.Id)).ToArray();
        if (selected.Length == 0 || selected.Length != options.Models.Distinct().Count()) throw new InvalidDataException("Selecione modelos de afilamento válidos.");
        using var cursor = new TableCursor(source);
        int Column(string name) => Array.IndexOf(cursor.Columns, name) is int i && i >= 0 ? i : throw new InvalidDataException("Coluna ausente: " + name);
        int tree = Column(options.Tree), dap = Column(options.Dap), total = Column(options.TotalHeight), h = Column(options.Height), d = Column(options.Diameter);
        if (new[] { dap,total,h,d }.Distinct().Count() != 4) throw new InvalidDataException("DAP, H, h e d devem usar colunas distintas.");
        int strat = options.Stratum.Length == 0 ? -1 : Column(options.Stratum);
        var rows = new List<Row>();
        double Number(object? value) => double.TryParse(TableCursor.Text(value).Trim().Replace(',','.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.NaN;
        foreach (var row in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();
            rows.Add(new(row, TableCursor.Text(row[tree]), strat < 0 ? "Global" : TableCursor.Text(row[strat]), Number(row[dap]), Number(row[total]), Number(row[h]), Number(row[d])));
        }
        if (!rows.Any(r => r.Valid)) throw new InvalidDataException("Não há medições válidas: confira ID, DAP > 0, H > 1,3 m, 0 ≤ h < H e d > 0.");
        var groups = rows.GroupBy(r => r.Group, StringComparer.Ordinal).ToArray();
        if (groups.Length > 1000) throw new InvalidDataException("Há mais de 1.000 estratos. Escolha outro campo ou divida a base.");
        var fits = new JsonArray(); var parameters = new List<object?[]>(); var statistics = new List<object?[]>();
        var fitted = new Dictionary<(string Group,string Model),double[]>(); int done = 0;
        foreach (var group in groups)
        foreach (var model in selected)
        {
            cancellation.ThrowIfCancellationRequested();
            var valid = group.Where(r => r.Valid).ToArray();
            var b = valid.Length > model.Initial.Length ? Fit(model, valid, cancellation) : null;
            string status = valid.Length <= model.Initial.Length ? "Observações insuficientes" : b == null ? "Sem convergência" : "OK";
            double[] estimates = b == null ? [] : valid.Select(r => Predict(model,b,r.Dap,r.H,r.h)).ToArray();
            double sse = b == null ? double.NaN : valid.Select((r,i) => Math.Pow(r.d-estimates[i],2)).Sum();
            double mean = valid.Length == 0 ? double.NaN : valid.Average(r => r.d);
            double sst = valid.Sum(r => Math.Pow(r.d-mean,2));
            double bias = b == null ? double.NaN : valid.Select((r,i) => r.d-estimates[i]).Average();
            var fit = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["model"] = model.Id, ["name"] = model.Name, ["formula"] = model.Formula,
                ["group"] = group.Key, ["n"] = valid.Length, ["trees"] = valid.Select(r => r.Tree).Distinct().Count(), ["invalid"] = group.Count()-valid.Length,
                ["status"] = status, ["r2"] = JsonExtensions.Finite(sst > 0 ? 1-sse/sst : double.NaN),
                ["rmse"] = JsonExtensions.Finite(Math.Sqrt(sse/Math.Max(1,valid.Length))), ["bias"] = JsonExtensions.Finite(bias),
                ["bias_pct"] = JsonExtensions.Finite(bias/mean*100), ["coefficients"] = b == null ? null : new JsonArray(b.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
            if (b != null) fitted[(group.Key,model.Id)] = b;
            object?[] slots = new object?[4];
            if (b != null) for(int i=0;i<b.Length;i++) slots[int.Parse(model.Parameters[i][1..])] = b[i];
            parameters.Add([group.Key,model.Name,model.Formula,status,valid.Length, ..slots]);
            statistics.Add([group.Key,model.Name,status,valid.Length,valid.Select(r=>r.Tree).Distinct().Count(),group.Count()-valid.Length,
                fit["r2"]?.GetValue<double>(),fit["rmse"]?.GetValue<double>(),fit["bias"]?.GetValue<double>(),fit["bias_pct"]?.GetValue<double>()]);
            fits.Add(fit); progress?.Report(new TableProgress(++done*90d/(groups.Length*selected.Length), $"Afilamento · {group.Key} · {model.Name}: {status}"));
        }
        if (fitted.Count == 0) throw new InvalidOperationException("Nenhum modelo convergiu. " + string.Join("; ", fits.Select(f => f!["name"]+": "+f!["status"])));
        var columns = cursor.Columns.ToList();
        foreach(var model in selected)
        {
            string name = "d_"+model.Id; int suffix=2;
            while(columns.Contains(name,StringComparer.OrdinalIgnoreCase)) name="d_"+model.Id+"_"+suffix++;
            columns.Add(name);
        }
        using var writer = new CompactBankWriter(output);
        string dataId=writer.AddTable(columns.ToArray()), paramId=writer.AddTable(["Grupo","Modelo","Formula","Status","N_ajuste","b0","b1","b2","b3"],"Parametros"),
            statsId=writer.AddTable(["Grupo","Modelo","Status","N","Arvores","Invalidas","R2","RMSE_cm","Bias_cm","Bias_pct"],"Estatisticas");
        foreach(var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            var estimates = selected.Select(model => fitted.TryGetValue((row.Group,model.Id),out var b) && row.Tree.Length>0
                ? JsonExtensions.Finite(Predict(model,b,row.Dap,row.H,row.h))?.GetValue<double>() as object : null).ToArray();
            writer.Add(dataId,[..row.Values,..estimates]);
        }
        foreach(var row in parameters) writer.Add(paramId,row);
        foreach(var row in statistics) writer.Add(statsId,row);
        writer.Complete();
        // Table names are the stable keys exposed by the bank, not its internal IDs.
        return new([new(output,"Estatisticas",statistics.Count)],fits,rows.Count,rows.Count(r=>!r.Valid));
    }
    private static double[]? Fit(TaperModel model, Row[] rows, CancellationToken cancellation)
    {
        int k=model.Initial.Length;
        double Clamp(int i,double v) => model.Id=="ormerod_diameter_model" || model.Id=="garay_model" && i==3 ? Math.Max(1e-6,v)
            : model.Id=="garay_model" && i==2 ? Math.Clamp(v,1e-6,.9999) : v;
        double Score(double[] b)
        {
            double sum=0;
            foreach(var r in rows) {double p=Predict(model,b,r.Dap,r.H,r.h); if(!double.IsFinite(p))return double.PositiveInfinity;sum+=Math.Pow(r.d-p,2);}
            return sum;
        }
        double best=double.PositiveInfinity; double[]? result=null;
        for(int start=0;start<3;start++)
        {
            var b=model.Initial.Select((v,i)=>Clamp(i,v*(start==0?1:start==1?.75:1.25))).ToArray();
            double score=Score(b),lambda=.001;
            for(int iteration=0;iteration<700;iteration++)
            {
                cancellation.ThrowIfCancellationRequested();
                var a=new double[k,k]; var rhs=new double[k];
                foreach(var row in rows)
                {
                    double prediction=Predict(model,b,row.Dap,row.H,row.h);var jac=new double[k];
                    for(int j=0;j<k;j++)
                    {
                        double old=b[j],delta=1e-5*Math.Max(1,Math.Abs(old));
                        double up=Clamp(j,old+delta),down=Clamp(j,old-delta);
                        b[j]=up;double p=Predict(model,b,row.Dap,row.H,row.h);
                        b[j]=down;double m=Predict(model,b,row.Dap,row.H,row.h);b[j]=old;
                        jac[j]=(p-m)/(up-down);
                    }
                    for(int j=0;j<k;j++){rhs[j]+=jac[j]*(row.d-prediction);for(int l=0;l<k;l++)a[j,l]+=jac[j]*jac[l];}
                }
                if(rhs.Any(v=>!double.IsFinite(v)))break;
                if(rhs.Max(v=>Math.Abs(v))<1e-8 && (score<1e-12 || rows.Select(r=>Predict(model,b,r.Dap,r.H,r.h)).Max()-rows.Select(r=>Predict(model,b,r.Dap,r.H,r.h)).Min()>1e-8))
                {if(score<best){best=score;result=(double[])b.Clone();}break;}
                for(int j=0;j<k;j++)a[j,j]+=lambda*Math.Max(1,a[j,j]);
                if(!InventoryMath.Solve(a,rhs,out var step)){lambda*=10;continue;}
                var trial=b.Select((v,i)=>Clamp(i,v+step[i])).ToArray();double next=Score(trial);
                if(next<score)
                {
                    double improvement=score-next;b=trial;score=next;lambda=Math.Max(1e-12,lambda*.3);
                    if(improvement<1e-11*Math.Max(1,score)) {if(score<best){best=score;result=(double[])b.Clone();}break;}
                }
                else {lambda*=10;if(lambda>1e16)break;}
            }
        }
        return result;
    }
}
