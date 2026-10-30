# Banco DLB do projeto

O formato DLB é um armazenamento autoral do DendroLab em C#/.NET. O projeto
`MeuProjeto.dendro` contém configuração, referências e hierarquia. O arquivo
**`MeuProjeto.dlb` é o único banco físico do projeto** e contém as tabelas de
junções, estratos, importações e resultados de processamento no mesmo catálogo.
Não há uma pasta `.dlb` com bancos separados por processamento.

Mova ou copie o `.dendro` e seu arquivo `.dlb` juntos. Se houver processamento
versionado, inclua também `Projeto.dendro.history.json`; veja [Processamento versionado](PROCESSAMENTO_VERSIONADO.md).
Os vínculos gerados usam
caminhos relativos. Salvar como copia o banco, preserva o original e atualiza os
vínculos do novo projeto. Um destino com banco já existente é recusado para não
substituir dados. Para gerar tabelas é necessário salvar o projeto primeiro.

Junção, estratificação e Processador gravam diretamente em DLB. Nenhum deles
gera Excel automaticamente. A opção Copiar para o banco do projeto traz uma
worksheet/tabela externa, com seu cabeçalho selecionado, sem alterar a origem.
Excel e CSV são exportações explícitas na visualização de tabelas.

Na visualização, **Excluir selecionadas** remove as linhas marcadas na página atual;
**Excluir filtradas** remove todos os resultados do filtro, inclusive nas outras
páginas. As ações pedem confirmação da quantidade, preservam as demais tabelas e
recusam a gravação se os dados exibidos tiverem sido alterados por outro usuário.
Estão disponíveis em tabelas DLB editáveis. Planilhas externas e versões do histórico
devem ser copiadas para o banco antes de editar, preservando os originais.

Datas e horas recebem formatação de exibição pelo nome da coluna e pelos mapeamentos
do inventário. O menu do botão direito no cabeçalho permite escolher Automático,
Número, Texto, Data, Hora ou Data e hora. A preferência fica no projeto; os valores
numéricos usados nos cálculos permanecem iguais.

O botão direito do cabeçalho também oferece **Remover coluna…** e **Remover colunas…**.
A remoção pode selecionar uma ou várias colunas e pede confirmação; atua na tabela
inteira, mesmo com um filtro ativo, sem remover linhas ou outras tabelas. É obrigatório
manter ao menos uma coluna. A proteção de versões históricas e de alterações concorrentes
é a mesma da exclusão de linhas.

## Organização binária, versão 2

- Assinatura `DNDRDB02` e posição de 64 bits do catálogo final.
- Blocos independentes com valores organizados por coluna antes da compressão
  Brotli. Cada célula distingue nulo, número de ponto flutuante e texto UTF-8;
  texto vazio continua diferente de nulo. Datas do Excel mantêm a representação
  numérica já usada pelo motor. NaN/infinito são armazenados como nulo.
- Catálogo JSON versionado com ID e nome da tabela, colunas, total de linhas e
  posição, tamanho, primeira linha, quantidade de linhas e SHA-256 de cada bloco.
- Paginação busca os blocos correspondentes sem percorrer os dados anteriores;
  o bloco é verificado antes de ser descomprimido. Índices incompletos, contagens
  inconsistentes e blocos alterados são recusados.
- Escrita com buffers compartilhados entre tabelas, alvo aproximado de 4 MiB e
  até 2.048 linhas por bloco. Uma linha excepcional pode chegar a 16 MiB. O uso
  real inclui objetos .NET, buffers de compressão e o catálogo. Não é uma cota
  fixa de memória do processo.

Junção, estratos e processamento usam uma área de trabalho temporária fora da
pasta do projeto. Ao concluir, a transação publica as tabelas no arquivo único
por substituição atômica. Os blocos comprimidos são copiados sem recompressão,
com validação SHA-256 e atualização dos seus deslocamentos. A publicação atual
copia os blocos existentes para um novo arquivo temporário; o custo cresce com
o tamanho do banco, mas não materializa suas linhas em memória. Falhas deixam
o banco anterior intacto. Não há suporte a escritores concorrentes.

Cada conjunto de tabelas tem um identificador de origem (`Dataset`) no catálogo;
referências internas como `MeuProjeto.dlb/Entrada.dlb` identificam esse escopo,
**não representam arquivos nem pastas reais**. Elas preservam os vínculos de
projetos anteriores. Na árvore aparecem tabelas, não novos bancos `.dlb`.
Reprocessar a mesma entrada no mesmo modo atualiza seu conjunto de resultados;
a árvore apresenta cada nome de aba uma vez, usando a versão mais recente.

Ao abrir um projeto com a organização antiga, a pasta é consolidada em um
arquivo no mesmo caminho e a pasta original é preservada com sufixo
`.legacy-backup-...`. A conversão verifica os blocos e mantém as referências.
Salvar como copia o arquivo único e remapeia os vínculos para o novo nome.
A edição de dados em linhas, índices espaciais,
geometrias/CRS e consultas SQL não fazem parte desta primeira versão. DLB não é
compatível com File Geodatabase da Esri.

## Memória e desempenho

O navegador usa páginas de 500 linhas. Junção, estratificação, importação e
exportação tabular fazem leitura sequencial. O Processador, gráficos, regressões
e alguns testes ainda materializam seus dados de trabalho; armazenar em DLB não
transforma esses algoritmos em processamento integral por blocos.

O modo opcional **Resumo direto** do Processador evita as listas completas de
árvores e os conjuntos do relatório estatístico. A saída por árvore, se
selecionada, é gravada sequencialmente no escritor DLB; ficam retidos agregados
por parcela/ano para os níveis superiores. Entrada e preparação da RNA ainda
são materializadas. Os níveis selecionados geram tabelas `DIRETO - ARVORES`,
`DIRETO - PARCELAS`, `DIRETO - TALHOES`, `DIRETO - LOTES` e/ou `DIRETO - PROJETO`,
além de `INFORMACOES`. O modo completo mantém suas abas configuradas de relatório.

Medição local em 24/10/2026, Release/.NET 10: junção de duas origens sintéticas,
com as mesmas 100.000 linhas e seis colunas em cada formato resultante. Valores
incluem estratos repetidos e medições numéricas; não representam todo inventário.

| Saída | Bytes | Tempo desta execução |
| --- | ---: | ---: |
| DLB | 222.494 | 2.506 ms |
| XLSX | 3.149.775 | 1.896 ms |
| Banco antigo LiteDB | 23.314.432 | 2.136 ms |

Nesse conjunto, DLB ocupou cerca de 93% menos espaço que XLSX. A gravação
comprimida levou mais tempo nesta execução; não se declara ganho universal de
velocidade. Abrir a página das linhas 99.501–100.000 alocou 572.576 bytes na
thread medida, incluindo página e descompressão, sem carregar as 100.000 linhas.
Tempos incluem leitura das origens e estágio intermediário, não são uma
comparação isolada de codecs. Não foi medido ArcGIS/Esri GDB.

Métricas originais:
`.build/storage-probe/results-a33afff7f2f14fe482a0028a1c759d44/metrics.json`.
Os testes cobrem tipos, Unicode, múltiplas tabelas, fronteiras entre blocos,
cabeçalho alternativo, cancelamento, corrupção, paginação tardia e movimentação
do projeto com seu banco. Bancos antigos `.dendrodb` permanecem compatíveis.

A medição de gravação acima precede a otimização de publicação direta do estágio comprimido. A junção DLB atual evita a segunda descompressão/recompressão, informa andamento com atualização limitada a aproximadamente 150 ms, permite cancelamento no rodapé e registra resultados em uma única atualização do projeto. O teste de interface adicional une 32 fontes (262.144 linhas), verifica atendimento do dispatcher e cancela uma segunda operação sem publicar saída parcial.
