# Processamento completo: versões, fatores e aprovação

## Uso

1. Salve o projeto. Confira os mapeamentos e as chaves de árvore/parcela em **Configurações > Identificação**.
2. No Processador, selecione **Inventário com revisão e aprovação**, indique IFC, IFPC ou MISTO e execute.
3. O resultado original V1 é publicado em **Aguardando revisão**. Abra **Revisão de inventário**, selecione essa versão e clique em **Iniciar revisão**.
4. Consulte as tabelas por árvore e parcela. Para aceitar o resultado operacional sem ajuste, aprove a versão.
5. Para pedir uma correção, informe a justificativa e clique em **Solicitar ajustes**. O motivo fica no histórico da versão.
6. Na versão devolvida, selecione o escopo por talhão ou lote e o método do fator:
   - **Manual:** informe a fração de aproveitamento entre 0 e 1.
   - **Calculado:** informe o volume operacional observado em m³ por grupo. O fator é calculado como volume observado dividido pelo VTCC inventariado. Valores acima de 1 são aceitos e destacados para conferência.
7. Informe a justificativa do ajuste e clique em **Criar versão corrigida e reenviar**. A nova versão usa sempre a V1 como base e volta para **Aguardando revisão**. Ela preserva os volumes inventariados, registra os fatores e acrescenta colunas de volume operacional; DAP e altura não são alterados.
8. Inicie a revisão da versão nova e aprove somente quando estiver pronta. A aprovação exige a versão mais recente e não permite fatores editados ainda não publicados.
9. Exporte o Excel somente da versão aprovada vigente. A aba PROCESSAMENTO acompanha a versão, o método, o responsável e a aprovação.

### Volumes estimados pelo taper

No mesmo Processador, habilite **Calcular volumes pelo ajuste de taper**, selecione um conjunto salvo com status OK e configure altura do toco, comprimento máximo e mínimo das toras, Dmin e descarte da última tora curta. Para habilitar a comparação com VTCC/VCCC, confirme que os diâmetros usados no ajuste foram medidos com casca. Em ajustes estratificados, o processamento usa a coluna de estrato gravada no ajuste e exige esse cabeçalho nos inventários selecionados.

O processamento grava por árvore e soma por parcela/grupo **Volume_Taper_Total_m3** (volume modelado acima do toco), **Volume_Taper_Comercial_m3** (volume das toras que atendem às regras configuradas) e as diferenças para VTCC e VCCC. Esses números são estimativas baseadas no perfil de taper e nas regras de seccionamento. Eles não substituem os volumes das equações nem definem ou aplicam o fator operacional. O conjunto de coeficientes e as regras são congelados no processamento para permitir revisão e reprodução posterior, tanto no modo com revisão quanto no resumo direto.

O usuário do Windows é registrado no histórico, mas o DLB não aplica papéis de processador, revisor ou aprovador. Qualquer usuário com acesso ao projeto pode iniciar uma revisão e aprovar uma versão.

## Dados guardados

No app, **H Ajuste** e **ESTATISTICAS INV** abrem relatórios com abas **Informações** e **Gráficos**, filtros por lote/talhão e indicadores próprios. O Excel mantém sua apresentação de relatório. Os diagnósticos de altura usam os indicadores já calculados e até 20.000 pares por amostragem uniforme para desenhar os gráficos, com eixos em metros. A tabela auxiliar **Graficos** deixa de ser gerada no banco e é ocultada nos Conteúdos de versões antigas.

**AJUSTADO** no banco inclui todas as colunas produzidas durante o processamento, sem exigir debug. Para versões antigas do histórico, o app pode recuperar as colunas da entrada completa congelada na V1; quando essa entrada não existe, é necessário reprocessar. As rotinas de aplicação e preenchimento de altura permanecem iguais.

Quando o fallback de plantio está ativo, o padrão normal de códigos como `RDBOBA00413P16-147` é lido diretamente, sem regex, e os códigos repetidos são resolvidos uma única vez por execução. Padrões personalizados continuam disponíveis. A verificação reúne todas as pendências antes de alterar o cadastro em memória. Em caso de erro, abre **Pendências de plantio**, uma tabela temporária de consulta, com chave, código, primeira linha processada, quantidade e motivo. É possível filtrar e exportar esse relatório; ele não grava um processamento parcial no banco. A primeira linha é relativa à entrada já preparada para o processamento, após eventual remoção de duplicadas.

Um código como `CON-232` não contém um ano identificável pelo padrão normal; o sufixo `232` não é tratado como ano. Se sua chave tiver data de plantio no cadastro, não é necessário inferir o ano. Não se excluem registros automaticamente quando o plantio não pode ser resolvido.

A leitura padrão aceita espaços antes e depois do hífen, incluindo espaços não separáveis: `RDIPMA00437P07 -719` e `RDIPMA00437P07 - 719` usam o ano 2007. Essa tolerância é aplicada somente à extração do ano; a chave original permanece igual na tabela e no cadastro.

As áreas das parcelas são verificadas antes do cadastro, RNA e volumes, após eventual remoção de duplicadas. O cálculo existente de comprimento/largura e correção pelas declinações é preservado e seu resultado é reutilizado no processamento. Áreas não positivas ou não finitas interrompem a execução e abrem **Pendências de área**, com todos os grupos inválidos, talhão, parcela, medição, Fonte, nomes das colunas mapeadas, valores originais, área calculada, quantidade e motivo. A consulta é temporária, paginada e exportável; não exclui linhas nem substitui medidas automaticamente.

- Resultados por árvore e parcela de cada versão. As chaves são configuráveis. O padrão da árvore usa talhão, parcela, cova, fuste, fila e data; o da parcela usa talhão, parcela e data. Origem da base e tipo IFC/IFPC acompanham ambas. A opção de remover duplicadas mantém a primeira ocorrência e registra a quantidade removida; desmarcada, duplicatas impedem a publicação.
- Parcelas com medições em datas diferentes ficam em linhas separadas, mesmo no mesmo ano.
- IDs do inventário e processamento, número de versão, IFC/IFPC, usuário, datas, estado e eventos de aprovação.
- VTCC inventariado e operacional, método e fator aplicado. As tabelas de árvores, parcelas e talhões preservam colunas de volume inventariado e volume operacional. Na parcela, o fator agregado é a razão entre os volumes operacional e inventariado quando o inventariado é positivo.
- Entrada e configuração congeladas para recompor relatórios. Essas tabelas internas não aparecem nos seletores e na exportação.
- A receita interna completa fica na V1. As revisões referenciam o original, evitando repetir a entrada e a configuração congeladas em todas as versões.
- Revisões posteriores não substituem resultados anteriores. A linha do tempo não aceita ramificações: uma revisão deve partir da versão mais recente. As tabelas históricas não aceitam remoção nem edição direta. Para análises com colunas adicionais, copie a tabela para outra tabela do projeto.

O histórico começa nas novas execuções completas. Resultados antigos continuam acessíveis como resultados legados; não é inventada uma configuração histórica que não tenha sido guardada.

## Configuração das chaves e bases mistas

Em **Configurações > Identificação**, a grade seleciona as colunas da chave de árvore,
da chave de parcela e da origem da base. As listas podem usar campos mapeados do
inventário ou nomes de outras colunas já existentes na tabela. O botão de consultar
colunas carrega os cabeçalhos da origem sem percorrer suas linhas. A coluna **Fonte**,
criada por Juntar tabelas, é a origem padrão; pode ser trocada ou combinada com outras.

O tipo IFC/IFPC faz parte da separação mesmo quando não é marcado nas listas de chaves.
A coluna de tipo configurada tem prioridade. No automático, são procuradas colunas
de tipo conhecidas e depois IFC/IFPC no nome da Fonte; a escolha do Processador é a
alternativa quando não há indicação por linha. **MISTO** exige que essa indicação
seja resolvida em cada linha. Árvores, parcelas, estatísticas, resumos e fatores de
revisão mantêm a separação por base e tipo; as estatísticas usadas pela RNA também
respeitam as parcelas e bases identificadas. Cada versão congela a configuração usada.
Resultados antigos que misturaram bases precisam ser reprocessados; o histórico
anterior permanece preservado.

Em **Configurações > Campos do inventário > Declinações vazias**, marque separadamente
as opções de comprimento e largura para substituir células vazias por zero durante
o processamento. Ambas começam desmarcadas. Valores não numéricos preenchidos
continuam sendo reportados como inválidos. As unidades e a aplicação de DAP/H não
são alteradas por essas opções.

## Pasta de rede

A base compartilhada usa um único arquivo `Projeto.dlb`, com configurações,
tabelas e catálogo do histórico no mesmo contêiner. Aprovações e resultados de
processamento são publicados automaticamente, sem exigir Salvar projeto depois
de cada aprovação. Clientes abertos consultam atualizações a cada cinco segundos,
quando não estão executando uma operação. O botão Atualizar histórico força a consulta.

O processamento e o cálculo dos fatores usam uma área temporária local. A publicação no projeto usa bloqueios de arquivo e substituição atômica. O cálculo pode ocorrer em paralelo; gravações são coordenadas. O DLB copia os blocos existentes ao publicar conjuntos, portanto o tempo de publicação cresce com o arquivo e com a velocidade da rede.

A gravação mostra o nome da tabela e as linhas gravadas. A publicação no banco mostra os MB copiados. Ambas permitem cancelamento, descartando o arquivo temporário e preservando o banco anterior quando a publicação ainda não foi concluída. A largura de cada tabela é calculada uma vez por gravação, evitando percorrer todas as linhas a cada linha escrita. O formato DLB permanece compatível com os bancos existentes.

Se dois usuários publicarem simultaneamente a mesma próxima revisão, um publica e o outro recebe um conflito. Os fatores permanecem na grade para reaproveitamento. Uma aprovação sobre uma revisão desatualizada também é rejeitada. O salvamento geral do projeto detecta alterações de outro usuário, em vez de sobrescrevê-las silenciosamente.

Arquivos `.lock` e `.write.lock` podem permanecer na pasta. O bloqueio ativo é mantido pelo sistema operacional enquanto o arquivo está aberto, e não pela simples existência do arquivo. Todos os clientes devem usar a versão atual do aplicativo.

Os testes de concorrência locais não substituem uma validação com duas estações na pasta SMB real, incluindo suas permissões, latência e interrupções de conexão.
