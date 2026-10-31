# Arquitetura C# e migração

## Componentes

- **Dendro.Desktop** (`net10.0-windows`, WPF): layout AvalonDock, documentos,
  formulários, tabelas e renderização interativa por DrawingContext.
- **Dendro.Core** (`net10.0`): biblioteca independente da interface, com serviços
  tipados para planilhas, fórmulas, processamento de inventário, RNA, regressões
  e exportação. Nenhum P/Invoke, subprocesso Python ou biblioteca nativa do motor.
- **ProjectSession**: estado do editor, referências, alterações pendentes e
  identidade dos gráficos. A configuração em JSON fica em uma tabela interna do
  contêiner DLB e preserva seções desconhecidas.
- **WorkspaceOperations**: comandos locais da aplicação sobre cópias do projeto.
  Chama diretamente os serviços C#; não serializa mensagens para uma DLL ou
  processo externo. O trabalho numérico roda fora do dispatcher WPF.
- **ProjectStore/ProjectModels**: validação, modelos, histórico e gravação atômica.
- **InventoryProcessor/InventoryReport**: volume individual antes das somas,
  parcelas, talhões, estatísticas, auditoria e construção das abas.
- **NeuralModel/HeightProcessor**: pesos HDF5 gerenciados, categorias,
  normalização, inferência float32 e calibração por viés, linear ou Ridge.
- **Formula**: parser limitado que compila a gramática matemática em delegates.
  Fórmulas não executam scripts, imports nem comandos do sistema.
- **RegressionEngine**: polinômios com X normalizado e QR; modelos não lineares
  com Levenberg–Marquardt e múltiplas sementes; fronteiras por estrato.
- **WorkbookWriter**: Open XML, estilos, destaques e gráficos editáveis no Excel.

## Compatibilidade

O contrato `DENDRO_PROJECT` continua na versão 1. O arquivo original só é
reescrito quando o usuário salva. Configurações e fórmulas inválidas não são
aplicadas parcialmente ao editor. Relatórios são criados em arquivo temporário
na pasta de destino e publicados somente após conclusão. O processador recusa
sobrescrever entradas ou resultados existentes.

A seleção de worksheet/cabeçalho é por arquivo. Os caminhos relativos são
resolvidos a partir do projeto antes de processar. Resultados de cada lote
são adicionados aos Conteúdos.

## Verificação da migração

`build.ps1 -Test` executa os testes C# e os testes WPF com dados versionados em
`tests/Fixtures`. As comparações congeladas cobrem relatório básico, duas
versões de crescimento, todos os campos renomeados, preditores multivariados,
fator e RNA com finetune. Verificam abas, células e estrutura Open XML, com
limiar numérico de 1e-8 nos relatórios complexos e 1e-9 no básico.

O teste adicional `DENDRO_REFERENCE_ZIP` confronta a previsão do ZIP real
H_geral_20261019_111754_diagnostico.zip com a previsão C de 247.638275.
A interface testa cadastro de modelos, painéis, seleção de pontos, desfazer,
comparação, fronteira, exportação, persistência e cópia de projeto existente.
Também cobre nomes de exibição persistentes, abertura de bancos e estratificação.
O fechamento é confirmado fora do evento `Closing` do WPF, evitando a chamada
reentrante de `Close` que causava o erro de Visibility. Os cenários incluem
confirmação imediata, cancelamento, confirmação assíncrona, cliques repetidos e
painéis flutuantes/ocultos. Erros inesperados da interface são registrados em
`%LOCALAPPDATA%/DENDRO/errors.log`.

As limitações do processamento anteriores à migração estão em `PARIDADE.md`.
Não foi acrescentado Taylor, treinamento CALIBRATE nem alegação de paridade
integral com IFPC.

## Organização e histórico

A solução ativa contém apenas projetos .NET. Fontes C, vendor, scripts antigos,
executáveis e licenças anteriores foram preservados no ZIP oculto
`.archive/DENDRO-C-reference-20261023.zip`, com manifesto SHA256.
A ferramenta C/Zig/HDF5 não faz parte da compilação nova. O projeto de teste
migrado está em `projects/teste.dlb`.

Intermediários ficam em `.build`, sem diretórios bin/obj espalhados nos fontes.
Abra `DENDRO.slnx` ou use o ponto de entrada `build.ps1`.
