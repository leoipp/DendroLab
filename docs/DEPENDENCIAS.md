# Dependências redistribuídas

A aplicação usa apenas assemblies gerenciados das seguintes bibliotecas:

| Pacote | Versão | Uso | Licença do pacote |
| --- | --- | --- | --- |
| Dirkster.AvalonDock | 4.74.1 | Painéis e documentos WPF | MIT |
| Dirkster.AvalonDock.Themes.VS2013 | 4.74.1 | Tema dos painéis | MIT |
| ExcelDataReader | 3.9.0 | Leitura XLS/XLSX | MIT |
| LiteDB | 5.0.21 | Compatibilidade com bancos antigos .dendrodb | MIT |
| MathNet.Numerics | 5.0.0 | Distribuições t, F e probabilidades dos testes | MIT |
| PureHDF | 2.2.0 | Leitura dos pesos HDF5 em C# | MIT |
| DocumentFormat.OpenXml | 3.5.1 | Escrita XLSX | MIT |
| DocumentFormat.OpenXml.Framework | 3.5.1 | Infraestrutura Open XML | MIT |

A publicação copia os avisos e metadados de licença dos pacotes para
`app/licenses`. O runtime WPF/.NET 10 x64 é instalado separadamente.
As ferramentas de desenvolvimento/teste não são dependências do aplicativo.
O ZIP antigo em `.archive` mantém os avisos das bibliotecas C da versão retirada.

Fontes: [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader),
[PureHDF](https://github.com/Apollo3zehn/PureHDF),
[Open XML SDK](https://github.com/dotnet/Open-XML-SDK),
[AvalonDock](https://github.com/Dirkster99/AvalonDock),
[LiteDB](https://github.com/litedb-org/LiteDB),
[Math.NET Numerics](https://numerics.mathdotnet.com/Probability).
