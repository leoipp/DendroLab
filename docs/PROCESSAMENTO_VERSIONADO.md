# Processamento completo: versões, fatores e aprovação

## Uso

1. Salve o projeto. Em Configurações, confira os mapeamentos do inventário. Em **Identificação**, escolha as colunas das chaves de árvore e parcela e as colunas que identificam cada base.
2. No Processador, selecione **Inventário com revisão e aprovação**, indique **IFC**, **IFPC** ou **MISTO** e o nível inicial dos fatores: talhão ou lote. Em bases mistas, configure a coluna que indica IFC/IFPC em cada linha ou use Fontes com nomes que identifiquem o tipo.
3. Execute. A V1 é sempre original, com fator 1, e é publicada no histórico. O Resumo direto mantém seu fluxo anterior.
4. Abra **Revisão de inventário**, ou clique com o botão direito na versão nos Conteúdos.
5. Abra as tabelas de árvores e parcelas para avaliar o resultado. Se não for necessário corrigir, aprove a V1.
6. Para corrigir, preencha fatores entre 0 e 1 na grade. Também é possível exportar a tabela de fatores, preenchê-la no Excel e importá-la. Não altere o ID de origem, nível ou códigos.
7. Clique em **Criar nova versão corrigida**. O app sempre aplica o fator ao VTCC da V1, nunca ao volume já corrigido. Alturas e suas informações de ajuste são reaproveitadas.
8. A aprovação usa o usuário do Windows; qualquer usuário pode aprovar nesta versão. Só uma aprovação fica vigente por inventário. Aprovações anteriores continuam no histórico.
9. Selecione as abas e exporte o Excel da versão aprovada vigente. A aba PROCESSAMENTO identifica a versão, o responsável e a aprovação e acompanha o arquivo.

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
- VTCC original e corrigido e fator aplicado. Na parcela, o fator agregado é a razão entre os volumes corrigido e original quando o original é positivo.
- Entrada e configuração congeladas para recompor relatórios. Essas tabelas internas não aparecem nos seletores e na exportação.
- A receita interna completa fica na V1. As revisões referenciam o original, evitando repetir a entrada e a configuração congeladas em todas as versões.
- Revisões posteriores não substituem resultados anteriores. As tabelas históricas não aceitam remoção nem edição direta. Para análises com colunas adicionais, copie a tabela para outra tabela do projeto.

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
