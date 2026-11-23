# Modelos de Afilamento

Abra **Inventário → Modelos de Afilamento**. A ferramenta usa tabelas do projeto (Excel ou DLB), com seleção da tabela/aba e mapeamento de ID da árvore, DAP, altura total H, altura da seção h e diâmetro d. DAP e d devem estar em centímetros; H e h, em metros. O identificador deve distinguir as árvores da base; se necessário, prepare uma chave composta antes do ajuste.

O ajuste pode ser global ou separado por um campo de estrato. Selecione os modelos desejados: Kozak, Demaerschalk, Ormerod, Garcia e Garay. A aba Equações apresenta as cinco definições. As equações, valores iniciais e restrições de Ormerod e Garay foram conferidos na referência local IFPC (`tools/taperadjust_screen.py`, `cli/TaperAdjust.py` e a seção `taper_adjust.models` de `config.json`).

O cálculo é nativo C#, sem executar o IFPC ou instalar Python. Usa mínimos quadrados não lineares com amortecimento, diferenças finitas, múltiplos valores iniciais e limites de parâmetros. Como o otimizador difere do SciPy da referência, os coeficientes não precisam ser numericamente idênticos.

## Resultados

Salve o projeto antes de executar. Cada execução cria um novo conjunto de resultados, sem sobrescrever os anteriores, com três tabelas no banco do projeto:

- **Dados:** medições originais e uma coluna estimada por modelo. Colisões com nomes de colunas existentes recebem sufixos.
- **Parametros:** coeficientes, fórmula, grupo, quantidade válida e status de convergência. Ormerod usa b1; coeficientes inexistentes ou ajustes sem convergência ficam vazios.
- **Estatisticas:** N, árvores distintas, medições inválidas, R², RMSE em cm, Bias em cm e Bias em porcentagem. Bias = média de observado menos estimado, como na referência.

A aba **Ajustes salvos** permite consultar o catálogo, abrir as três tabelas e exportar o conjunto para XLSX com as três abas. Os registros preservam origem, tabela, mapeamentos, estrato, fórmula, coeficientes e data. Salve o projeto para persistir o catálogo.

Para ajustar, são válidas as observações com ID preenchido, números finitos, DAP > 0, H > 1,3 m, 0 ≤ h < H e d > 0. Medições inválidas continuam na saída e são contabilizadas. As estimativas podem ser calculadas para linhas sem d observado, desde que o ID e as entradas do modelo sejam válidos. No topo h = H, a estimativa é mantida somente quando a equação produz um número finito. Campo de estrato vazio é tratado como um grupo separado, sem descartar suas linhas.

Não são aplicadas correções artificiais de monotonicidade ou positividade às curvas além da raiz truncada de Kozak/Garcia e dos limites de parâmetros definidos na referência. Ajustes insuficientes ou sem convergência são identificados no relatório. R² fica vazio quando não há variação nos diâmetros observados. Se nenhum modelo convergir, não é registrado um resultado parcial.

O cancelamento antes da importação impede o registro de resultados. O ajuste percorre todas as medições da tabela, sem teto fixo de linhas. Não realiza aplicação volumétrica, integração do fuste nem validação independente: as estatísticas descrevem o ajuste aos próprios dados de cubagem.

## Verificação

Testes com perfis sintéticos de cada uma das cinco equações verificam ajuste por estrato com parâmetros verdadeiros diferentes dos valores iniciais, RMSE, identificação de linhas inválidas e preservação das três tabelas. Outros cenários verificam mapeamento ambíguo e cancelamento. O teste desktop cobre a entrada pela aba Inventário, mapeamento de colunas, execução, importação e persistência do catálogo.
