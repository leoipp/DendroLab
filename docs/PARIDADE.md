# Paridade do núcleo C#

A implementação atual está em `src/Dendro.Core`, com interface WPF em
`src/Dendro.Desktop`. O código C anterior está arquivado como referência.
O aplicativo não depende dele em compilação nem execução.

| Regra | Implementação C# |
| --- | --- |
| Schumacher–Hall por árvore e fator | `InventoryMath`, `InventoryProcessor` |
| Área corrigida, regime, código e idade | `InventoryMath` |
| Entradas configuráveis e multivariadas | `ModelDefinition`, `ModelProgram` |
| Histórico de curvas e escolha vigente | `ProjectModels`, `InventoryProcessor` |
| Consolidação parcela/talhão | `InventoryProcessor` |
| Confiança, t de Student, erro e intervalo | `InventoryReport` |
| Debug, abas e auditoria de modelos | `InventoryReport` |
| XLS/XLSX, worksheet e cabeçalho | `SpreadsheetReader` |
| Estilos, destaques e gráficos no XLSX | `WorkbookWriter` |
| Inferência Dense, scalers e categorias | `NeuralModel` |
| Estatísticas por parcela e fórmulas | `FeaturePreparation`, `Formula` |
| Viés, linear, Ridge e fallback | `HeightProcessor` |
| Modelos de regressão e fronteiras | `RegressionEngine` |

Os testes comparam células de relatórios congelados gerados pelo C para sete
cenários, incluindo campos renomeados, fator, crescimento e finetune. Também
validam o esquema Open XML e a inferência de um modelo real exportado pelo IFPC.
Isso comprova os casos testados, sem garantir todos os dados e modelos possíveis.

Permanecem os limites anteriores:

- CALIBRATE não treina redes novas. O modo é recusado antes de gerar saída.
- RNA: Keras 3 Sequential Dense float32; não é um interpretador geral de Keras.
- O relatório possui AJUSTADO, H Ajuste, resumos, estatísticas, gráficos e
  auditoria; a reprodução de todos os histogramas/painéis visuais do IFPC
  original ainda não está completa.
- Falta uma comparação integral contra o IFPC original em bases reais completas.
- Taylor foi excluído por solicitação do usuário.

Os parâmetros efetivos e as chaves legadas estão descritos em
`AUDITORIA_IFPC_CONFIG.md`. O histórico da medição C × C# foi preservado em
`PERFORMANCE_C_CSHARP.md`; o ensaio isolado não mede o pipeline inteiro.
