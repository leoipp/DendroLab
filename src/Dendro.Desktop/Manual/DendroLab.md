# Primeiros passos
## Roteiro de trabalho
### Objetivo
O DendroLab organiza dados, processa inventários, ajusta modelos, faz diagnósticos e planeja parcelas. O projeto guarda as configurações e os resultados de trabalho; arquivos externos permanecem referenciados pelo caminho.
### Sequência recomendada
1. Crie e salve um projeto. Adicione o inventário e o cadastro florestal em Conteúdos. Confira a worksheet e a linha de cabeçalho de cada planilha.
2. Em Projeto → Configurações, mapeie colunas, unidades e chaves de identificação. Cadastre as equações necessárias em Volumetria e Crescimento e produção.
3. Revise dados em tabelas e gráficos. Execute o Processador no modo apropriado e consulte a Revisão de inventário quando houver fluxo versionado.
4. Compare resultados, investigue anomalias e documente as decisões. Exporte as tabelas ou a amostragem pelo menu correspondente.
### Exemplo
Ao juntar inventários anuais, a coluna Fonte identifica a origem. Incluí-la nas chaves de base pode impedir que parcelas de levantamentos diferentes sejam misturadas. Para pareamento temporal, escolha uma identidade permanente que continue válida entre anos.
### Navegação
Ferramentas abre os formulários no painel lateral. Tabelas, gráficos e mapas usam documentos no centro. O botão de voltar no título do formulário retorna à lista de ferramentas. Abra este manual no botão de Projeto ou com F1. Este manual funciona sem internet; Ctrl+F posiciona a busca e Esc fecha a janela.

## Projeto, banco e salvamento
### Como usar
Use Novo projeto, informe o nome e salve antes das operações que geram tabelas no banco. Abrir projeto recupera a estrutura guardada; Salvar registra mudanças e Salvar como permite outro destino.
### O que é guardado
As tabelas gerenciadas e os históricos de processamento usam o banco do projeto. As referências a planilhas, shapefiles e rasteres externos dependem dos arquivos originais. Não mova ou renomeie esses arquivos sem atualizar a referência.
### Cuidados
Salvar configurações não recalcula resultados anteriores. Reexecute a ferramenta quando quiser resultados com novos parâmetros. Uma revisão aprovada do inventário preserva seu histórico e exige o fluxo de revisão para alterações posteriores.
### Atualizações
Verificar atualizações consulta a versão publicada. O atualizador confere o arquivo baixado e oferece salvar o projeto antes de encerrar. A presença de uma versão nova no código-fonte não significa que um executável antigo já recebeu essa mudança.

## Conteúdos, grupos e camadas
### Organização
A árvore separa Planilhas, Bancos de dados, Dados vetoriais e Mapas base. Crie grupos de planilhas ou de vetores e mova itens para organizar projetos grandes. A pesquisa filtra os nomes e referências apresentadas.
### Como usar
Adicione arquivos pelo menu do grupo ou arraste-os para a aplicação. Uma inclusão revela o novo conteúdo, limpa a pesquisa e expande seu grupo. Duplo clique abre tabelas ou o mapa, conforme o tipo. O botão direito oferece abrir, renomear a referência, exportar quando aplicável e remover do projeto.
### Visibilidade no mapa
O checkbox antes de uma camada vetorial ou raster liga ou desliga sua exibição. Essa escolha é guardada no projeto; desmarcar não exclui o arquivo. A amostragem gerada é uma camada vetorial própria.
### Referências ausentes
O indicador de arquivo ausente significa que o caminho não foi encontrado. Use a opção de referenciar novamente e escolha o arquivo correto. Remover um item do projeto é diferente de editar seus dados; resultados com histórico protegido têm regras de remoção próprias.

## Tabelas: abrir, filtrar, ordenar e exportar
### Entrada e cabeçalho
No Excel, confirme a aba e a linha que contém os nomes das colunas. Uma linha de cabeçalho incorreta transforma títulos em dados ou altera os campos disponíveis nas ferramentas. A visualização trabalha em páginas para suportar bases extensas.
### Operações
Use filtros para localizar registros e ordenação para examinar valores extremos. Cópias e resultados gerenciados podem ser salvos no projeto; a exportação produz arquivos para uso externo. Confira se a operação escolhida altera a tabela gerenciada ou cria uma nova tabela.
### Interpretação
Ordenar a visualização não cria uma chave de identificação. Duplicatas devem ser avaliadas pelas colunas que representam a unidade observacional: duas árvores podem ter o mesmo DAP sem serem duplicatas.
### Exemplo
Para conferir uma parcela com volume alto, filtre pelo talhão, parcela e data. Compare a área da parcela e a quantidade de registros antes de concluir que existe erro de medição.

# Preparar e revisar dados
## Processador
### Finalidade e entradas
Transforma medições do inventário em resultados dendrométricos. Requer inventário, configuração de colunas e unidades, chaves de identificação e cadastro compatível. Quando o cálculo usa altura estimada ou volume, confira os modelos e as equações cadastrados.
### Como usar
Selecione as tabelas de entrada, o tipo IFC, IFPC ou MISTO, o modo de processamento e os níveis de resumo. No modo misto, a origem precisa permitir identificar o tipo de cada registro. Informe um prefixo reconhecível para os resultados e execute.
### Modos
Inventário com revisão e aprovação cria a versão original com fator 1. Revisões e aprovação seguem na ferramenta Revisão de inventário. Resumo direto produz os níveis habilitados e aceita o fator opcional informado nesse fluxo.
### Cálculos e unidades
A área seccional de um fuste, com DAP em centímetros, é `AS = π × DAP² / 40000`, em m². A soma por parcela dá a área basal amostrada; indicadores por hectare dependem da área efetiva da parcela. A equação volumétrica usa as entradas e unidades cadastradas, e não somente o rótulo da coluna.
### Resultados e conferência
As tabelas são guardadas no projeto, com saída por árvore, parcela ou níveis superiores conforme o modo. Verifique quantidades, áreas, alturas, modelo aplicado e alertas antes de usar os totais. Uma conversão errada de mm para cm afeta cálculos com DAP ao quadrado e pode produzir desvios muito grandes.

## Revisão de inventário
### Finalidade
Mantém versões rastreáveis do processamento completo. A V1 representa o cálculo original. A grade de fatores prepara uma nova versão, com motivo e identificação do usuário do Windows.
### Como usar
Selecione a versão, examine tabelas e relatórios e escolha o nível dos fatores, como talhão ou lote. Preencha os fatores e o motivo; publique a revisão e confira a versão criada antes de aprovar.
### Regra técnica
A revisão parte do VTCC original e aplica o fator adequado; não repete o ajuste de altura. Assim, uma segunda revisão não deve ser interpretada automaticamente como fator multiplicado sobre a revisão anterior.
### Aprovar e exportar
Aprovar atua sobre a versão selecionada, não sobre edições ainda não publicadas na grade. A exportação do inventário aprovado usa a versão vigente elegível e as abas marcadas. Versões aprovadas e cadeias com revisões posteriores possuem proteção contra remoção.
### Exemplo
Se o fator desejado passa de 0,95 para 0,92, registre 0,92 como novo fator de referência e publique a revisão correspondente. Confira no relatório qual versão está vigente antes de entregar o Excel.

## Auditoria pós-processamento
### Finalidade
Faz triagem de parcelas e confere coerência dos resultados com a origem. A auditoria não decide sozinha se uma parcela deve ser excluída.
### Entradas e parâmetros
Escolha o processamento ou a tabela de parcelas, a origem para conferência e as colunas do grupo de referência. Configure volume, DAP, altura, densidade, faixa de idade, mínimo de parcelas, multiplicador do IQR e limite de parcelas a conferir na origem.
### Como calcula
Dentro de grupos comparáveis, sinaliza valores extremos, inclusive acima de `Q3 + multiplicador × IQR`, densidades altas e inconsistências aritméticas. A parcela participa do próprio grupo. Grupos pequenos podem ficar sem limiar estatístico.
### Comparação temporal
As colunas da identidade permanente ligam medições. Configure a tolerância de queda de volume e confira datas e idades repetidas: uma sequência ambígua não deve ser tratada como evolução válida. Fonte pode mudar entre anos, portanto nem sempre pertence à chave temporal.
### Interpretação
Os limiares iniciais são parâmetros de triagem editáveis, sem significado de limite biológico universal. O relatório diferencia parcelas efetivamente conferidas na origem das que ficaram fora do limite de verificação. Investigue área, unidades, duplicatas, mortalidade e manejo antes de corrigir dados.

## Juntar tabelas
### Finalidade
Empilha linhas de várias tabelas em um resultado único. Não é um cruzamento relacional por código: os registros são acrescentados uns após os outros.
### Como usar
Selecione as origens, confira as colunas e escolha se usará somente os cabeçalhos comuns. Com essa opção marcada, campos exclusivos de uma origem ficam fora do resultado; desmarcada, as origens precisam cumprir a compatibilidade exigida pela ferramenta.
### Rastreabilidade
A coluna Fonte indica a tabela de origem de cada linha. Se já existir Fonte, os valores antigos são preservados como Fonte_anterior. Cabeçalhos vazios ou repetidos recebem nomes identificáveis, mantendo a ordem das colunas.
### Saída e exemplo
Informe o nome e grave a nova tabela no projeto. Ao unir IFC2025 e IFC2026, os registros permanecem separados por Fonte. Revise as chaves de identificação antes de processar a tabela unida para não somar medições de anos diferentes como uma única parcela.

## Parear medições
### Finalidade
Relaciona medições da mesma árvore ou parcela ao longo do tempo. Requer uma coluna de ano ou data e os campos numéricos que serão comparados.
### Identificação
As chaves vêm de Configurações → Identificação. A data é retirada da identidade para encontrar a mesma unidade em ocasiões distintas. O modo Ambos gera resultados separados para árvores e parcelas.
### Como usar
Escolha a tabela, a coluna temporal, as variáveis e o nível de pareamento. Selecione o formato de saída e a regra para registros repetidos. A opção que exige valor único interrompe a operação ao detectar duplicatas; uma agregação deve representar uma decisão metodológica consciente.
### Conferência
Uma troca de numeração de árvores entre campanhas pode criar pares incorretos mesmo com datas válidas. Confira exemplos conhecidos e unidades desaparecidas ou novas antes de calcular crescimento. Pareamento não prova que duas observações são independentes nem corrige mudanças de protocolo.

## Gerar coluna
### Finalidade
Cria campos por expressão ou concatenação. É útil para preparar códigos, grupos e variáveis antes de filtrar, estratificar ou processar.
### Como usar
Escolha a tabela e as colunas de origem, dê nome à nova coluna e escreva a expressão usando `[Nome da coluna]`. Revise a prévia e as definições antes de gravar. Expressão vazia concatena as colunas escolhidas com o separador informado.
### Funções e exemplos
`ESQUERDA([Código]; 6)` extrai os seis primeiros caracteres. `MEIO([Código]; 12; 3)` começa na posição 12 e extrai três caracteres: a contagem começa em 1. `SE([DAP] > 20; "Maior"; "Menor")` cria uma classificação.
O editor também oferece DIREITA, ANTES, DEPOIS, CONTEM, SUBSTITUIR, MAIUSCULA, MINUSCULA, APARAR, TAMANHO e operadores aritméticos; `&` junta textos. Use ponto nos decimais das expressões.
### Resultado
As colunas ficam disponíveis nas operações seguintes. Confira o comando de gravação: nas tabelas DLB selecionadas, a ferramenta permite gravar os campos na própria tabela, preservando as demais tabelas do banco. A prévia contém apenas uma pequena parte das linhas e não substitui a validação de toda a base.

## Estratificar
### Finalidade
Divide uma tabela em resultados pelos valores de uma ou mais colunas. Esta ferramenta organiza tabelas; a estratificação estatística da seleção GRTS é configurada na própria Amostragem.
### Como usar
Escolha a origem, selecione os campos de grupo com Ctrl ou Shift, aplique os filtros opcionais e liste os estratos. Marque os resultados desejados e confira suas contagens.
### Combinar estratos
Arraste a alça de um item sobre outro para combinar grupos. O destino fornece o nome do resultado, e as contagens são somadas. Essa união não torna populações diferentes homogêneas: é uma escolha de organização dos dados.
### Resultado e exemplo
Os resultados são salvos no banco do projeto. Uma separação por espécie e regime pode preparar análises mais comparáveis; uma separação por identificador de árvore tende a gerar tabelas pequenas e pouco úteis. Verifique espaços e grafias para evitar estratos duplicados como “Eucalipto” e “Eucalipto ”.

## Limpeza integrada
### Finalidade
Mostra os mesmos registros em dois ou mais gráficos vinculados. A seleção e a retirada de linhas se propagam entre os gráficos para ajudar a examinar relações entre DAP, altura, idade e outras variáveis.
### Como usar
Escolha a tabela e gráficos dessa mesma origem. Também é possível adicionar um gráfico pelos eixos X e Y. Clique ou desenhe um contorno para selecionar observações; confira sua posição nos outros gráficos antes de remover.
### Regra técnica
A sessão usa cópias dos gráficos e todas as linhas atuais da tabela. Exclusões anteriores dos gráficos não são importadas automaticamente. Remover seleção tira as mesmas linhas de todos os gráficos; Desfazer recupera a última remoção.
### Gravação
Salve ou exporte a tabela limpa antes de fechar a sessão temporária. Os gráficos originais e a tabela de origem não são a própria sessão de limpeza. Registre o critério adotado: remover pontos por aparência pode alterar a representatividade e os resultados do inventário.

## Ativo biológico
### Entradas
Escolha a tabela base, sua chave, o ID de curva e a data de plantio. No processamento, indique a chave correspondente, a data da medição, o volume observado em m³/ha e as colunas que deseja trazer. O tipo de inventário restringe a busca quando aplicável.
### Como calcula
Mantém as linhas da base e procura a maior data de medição por chave. O ID da base seleciona a curva vigente correspondente. A projeção usa `Y₂ = Y₁ × F(I₂) / F(I₁)`, tomando 1º de junho como data de referência de cada ano até o ano limite.
### Saídas
Ativo biológico reúne a base e os campos vinculados. Projeções mostra os anos futuros. IMA por idade calcula incrementos médios em idades inteiras desde o plantio. O prefixo ajuda a distinguir campos trazidos de campos já existentes.
### Conferência
Verifique chaves sem correspondência, empates na data mais recente, curvas ausentes, denominador nulo e idades fora do domínio do modelo. Projetar crescimento biológico não aplica automaticamente preço, custo, risco financeiro ou avaliação contábil.

# Gráficos e modelos
## Gráficos
### Configuração
Selecione tabela, eixos X/Y e, se necessário, um campo de estratos e filtros de grupo. Ajuste título, cor, tamanho e transparência dos pontos. Valores mínimos, máximos e intervalos de marcação dos eixos podem ser automáticos ou explícitos.
### Seleção de pontos
A seleção representa linhas da tabela. Um ponto sobreposto pode esconder outras observações; transparência e estratos ajudam a perceber a densidade. Confira a origem antes de remover linhas ou interpretar pontos ausentes por valores não numéricos.
### Recursos de interpretação
O violino resume a distribuição de Y em faixas de X. Quantidade de faixas, resolução, suavização e regra de outlier alteram a aparência da distribuição. Essas opções não equivalem a mudar os dados de origem.
### Saídas
Gráficos salvos registram a origem e a configuração no projeto. A geração em lote identifica a fonte no título. Para comparar figuras, use as mesmas unidades, limites e escalas; eixos automáticos diferentes podem exagerar diferenças visuais.

## Ajuste de regressão
### Entradas e execução
Parta de um gráfico, escolha a família da equação e mapeie Y e os preditores. Ajustes podem usar mais de um preditor conforme a família. Confira o número de coeficientes, as condições de domínio e as observações selecionadas.
### Regra técnica
O ajuste procura coeficientes que reduzam o erro entre observado e previsto, respeitando a formulação do modelo. Transformações logarítmicas e expressões não lineares possuem restrições: logaritmo de número não positivo, divisão por zero ou potência fora do domínio impedem avaliações válidas.
### Diagnóstico
Leia convergência, quantidade de pares válidos e resíduos, além das métricas. Um R² elevado não prova independência, ausência de viés ou capacidade de extrapolar. Examine resíduos por idade, tamanho e grupo, e use dados externos para avaliar predição.
### Salvar e reutilizar
Salve a regressão identificando origem, unidades e significado dos preditores. O cadastro em Volumetria, Crescimento e produção ou Índice de sítio exige mapear cada entrada para o contexto correto. Trocar a unidade de X sem transformar o modelo altera sua interpretação.

## Aplicar regressões
### Como usar
Selecione a tabela e uma ou mais regressões salvas. Cada regressão pode ter seu próprio mapeamento de X1, X2 e X3. Informe o nome da nova tabela e confira se as colunas são equivalentes às usadas no ajuste.
### Predição direta
Calcula `Y previsto = f(X)`. O Y observado é opcional nesse modo e serve como referência quando disponível. Coluna ausente ou valor não numérico produz resultado vazio naquela linha.
### Projeção entre ocasiões
No modo de projeção, calcula `Y₂ = Y₁ × f(i₂) / f(i₁)`. Y₁ é obrigatório e todos os preditores precisam ser mapeados nas duas ocasiões. O denominador precisa ser válido e diferente de zero.
### Resultado
A tabela original é preservada e as previsões vão para uma nova tabela. Antes de aplicar em toda a base, confira algumas linhas por cálculo independente, sobretudo se o modelo usa meses e a tabela está em anos.

## Ajustar rede neural
### Entradas e ambiente
Selecione a tabela, Y numérico, as variáveis explicativas e a validação. O treinamento desta ferramenta usa a integração existente com o ambiente local do IFPC; a pasta e o ambiente de treinamento precisam estar disponíveis. O planejamento de amostragem e GRTS executa em C#.
### Parâmetros
Camadas ocultas definem quantos neurônios existem em cada camada. Ativação determina a transformação não linear. Épocas limitam passagens pelos dados; lote define quantas observações participam de cada atualização. Taxa de aprendizado controla o passo de ajuste e paciência define a parada antecipada.
### Validação e dados
Preditoras categóricas são codificadas, e o encoder é guardado para aplicação futura. O fluxo de treinamento descarta valores ausentes. Estratificar treina modelos por grupo quando selecionado; grupos pequenos ou muito diferentes exigem avaliação própria.
### Saída e interpretação
O ZIP final guarda o modelo e seus metadados para aplicação no Processador. Mais camadas não garantem melhor previsão. Confira desempenho de validação, domínio das entradas e grupos não representados no treinamento. Uma divisão aleatória de linhas pode compartilhar a mesma parcela entre treino e validação; considere essa dependência ao interpretar as métricas.

## Ajustar Random Forest
### Finalidade
Treina um conjunto de árvores de regressão que combina previsões para reduzir a instabilidade de uma árvore individual. Usa o ambiente de treinamento integrado ao IFPC, com seleção de Y, preditoras e validação da ferramenta.
### Parâmetros
Número de árvores controla a quantidade de estimadores. Profundidade máxima limita a complexidade de cada árvore. Mínimo de amostras para divisão impede partições apoiadas em poucos registros. max_features controla as variáveis consideradas no crescimento das árvores.
### Como usar
Prepare os dados, escolha as preditoras que estarão disponíveis na aplicação e configure a validação. Compare combinações de parâmetros com a mesma separação de dados. Guarde o ZIP e confira seu desempenho em observações independentes.
### Limites
Árvores não descrevem necessariamente uma curva biológica suave. Bom desempenho dentro da base não garante extrapolação para idades ou materiais não observados. Identificadores podem memorizar grupos em vez de explicar a resposta; evite usá-los como preditores sem justificativa.

## Ajustar Gradient Boosting
### Finalidade
Combina árvores sequenciais: cada etapa busca corrigir parte dos erros das etapas anteriores. Requer o ambiente de treinamento integrado, Y, preditoras e configuração de validação.
### Parâmetros
O número de estimadores e a taxa de aprendizado atuam juntos. Uma taxa pequena costuma exigir mais etapas; profundidade e mínimo de amostras controlam a complexidade. Subsample usa uma fração dos dados em cada etapa, quando aplicável ao algoritmo configurado.
### Como usar e interpretar
Compare erro de treinamento e validação; melhoria somente no treinamento pode indicar sobreajuste. Repetir tentativas e escolher a melhor no mesmo conjunto de validação também adapta a decisão àqueles dados.
### Saída
O modelo é entregue no ZIP com informações para aplicação futura. A compatibilidade entre colunas, codificação de categorias e unidades precisa ser mantida. Não interprete a soma de árvores como uma equação volumétrica de poucos coeficientes.

## Ajustar SVR
### Finalidade
Treina uma regressão por vetores de suporte, com função de kernel. Usa o ambiente local de treinamento integrado e os mesmos cuidados de preparo e validação das outras ferramentas de aprendizado.
### Parâmetros
Kernel define a forma da relação: linear, polinomial ou outra opção disponível. C regula a penalização dos desvios. Épsilon define a faixa de erro tolerada sem penalização. Grau atua no kernel polinomial; parâmetros sem efeito no kernel escolhido devem ser interpretados nessa condição.
### Como usar
Mapeie Y e preditoras, configure validação e compare escolhas de C, épsilon e kernel com os mesmos dados. A escala das variáveis influencia métodos de distância; use o fluxo de preparo guardado junto ao modelo para aplicação.
### Limites
Um C muito alto pode adaptar demais o modelo ao treinamento. O valor de épsilon está ligado à escala da resposta e não deve ser interpretado como percentual automaticamente. Confira o modelo fora da amostra antes de escolher pela aparência da curva.

## Fronteira de eficiência
### Objetivo
Extrai uma borda superior ou inferior dos dados de um gráfico, por faixas de X. Representa uma tendência extrema do conjunto escolhido; não estima automaticamente eficiência causal ou potencial biológico verdadeiro.
### Parâmetros
Escolha lado da fronteira, método de cálculo, divisão das faixas, número de faixas e mínimo de observações. Os métodos podem usar máximos/mínimos, percentis ou médias das caudas. O percentil superior determina o correspondente inferior; a porcentagem da cauda controla quantas observações extremas entram na média.
### Regra de cálculo
Máximos e mínimos mantêm o X observado. Percentis usam o X médio da faixa; médias de cauda usam o X médio dos pontos selecionados. Faixas com observações insuficientes são omitidas.
### Conferência
A fronteira respeita X, Y, estratos, filtro e exclusões do gráfico de origem. Muitas faixas podem tornar a curva instável; poucas faixas escondem variação. Compare parametrizações e confirme que os extremos não são erros de área, unidade ou identificação.

# Comparar e validar
## Comparar curvas de produção
### Entradas
Escolha uma curva base e as candidatas, confirme a unidade de idade de cada ajuste e a unidade comum da produção. Defina idade mínima, máxima, idade de referência e tolerâncias práticas relativa e absoluta.
### Comparação das curvas
A ferramenta avalia as funções numa faixa comum e descreve diferenças entre as previsões. Uma tolerância prática responde se a diferença tem importância operacional; não equivale ao nível de significância de um teste.
### Com dados observados
Opcionalmente indique uma base comum, suas colunas de idade, produção, ano e as unidades independentes. O corte da base histórica ajuda a distinguir observações antigas de novas. Sem unidades identificadas, o relatório é descritivo.
### Cuidados
Talhão e parcela podem formar uma unidade, mas observações da mesma parcela ao longo do tempo não são réplicas independentes. Dados que participaram do ajuste recente não são validação externa desse ajuste. Diferenças também podem refletir sítio, manejo, idade ou composição da amostra.

## ICA e IMA
### Finalidade e entradas
Compara incrementos de curvas ajustadas ou cadastradas. Para ajustes de gráficos, confirme que X representa idade e Y produção acumulada; indique anos ou meses. O intervalo de análise é informado em anos.
### Fórmulas
`IMA(t) = V(t) / t`. O ICA instantâneo usa derivada numérica em relação à idade em anos. O ICA anual usa `V(t) − V(t − 1 ano)`, quando as avaliações existem. As unidades originais dos coeficientes são respeitadas na avaliação da equação.
### Resultados
O gráfico mostra IMA e ICA e marca cruzamentos. O relatório registra faixa, extremos e cruzamentos encontrados. Uma idade de cruzamento do ICA por diferença anual pode não coincidir com o máximo exato do IMA contínuo.
### Interpretação
Um cruzamento pode representar máximo ou mínimo; confirme a forma da curva. Ausência de cruzamento significa ausência detectada no intervalo analisado. Curvas coincidentes não fornecem uma idade única de culminação, e valores negativos ou não finitos precisam ser investigados.

## Testes estatísticos
### Escolha da pergunta
Use t de Welch para comparar médias de amostras independentes sem impor variâncias iguais. Use t pareado para diferenças de pares correspondentes, que a ferramenta organiza pela mesma linha das duas colunas. ANOVA compara médias de vários grupos sob seus pressupostos.
O teste F de variâncias compara dispersões e é sensível à normalidade. Brown–Forsythe fornece outra abordagem de comparação das variâncias, baseada nos desvios em relação ao centro dos grupos.
### Como usar
Escolha a tabela, o teste, a organização das amostras, a coluna de valores e os grupos. Liste e marque os grupos e informe α entre 0 e 1. Confira o número de pares ou observações válidas.
### Identidade de regressões
Seleciona uma família 1P, X, Y e grupos. Compara um ajuste comum com os ajustes separados. A estatística usa a diferença entre somas de quadrados e os graus de liberdade. Exige ajustes válidos, identificáveis e convergentes; nas famílias não lineares gerais, a inferência F é uma aproximação.
### Interpretação
Um p-valor pequeno indica incompatibilidade dos dados com a hipótese nula sob os pressupostos. Não mede tamanho do efeito e não prova causalidade. Não rejeitar a hipótese não demonstra equivalência. Confira independência, pareamento e distribuição dos resíduos; remedições não são automaticamente independentes.

## Validação de modelos
### Entradas
Escolha uma coluna observada e uma ou mais colunas de estimativas da mesma tabela. Cada modelo usa seus próprios pares numéricos válidos; diferenças de quantidade podem afetar a comparação.
### Métricas
Erro é `estimado − observado`. Bias é a média desse erro na unidade da resposta. MAE usa erros absolutos; RMSE penaliza mais os grandes desvios. MAPE usa erros percentuais absolutos e exige atenção a observados nulos ou muito pequenos.
`R² = 1 − SQ(resíduos) / SQ(total)` compara a estimativa com a média observada e pode ser negativo. R² negativo significa desempenho inferior a essa referência naquela base.
### Gráficos
A dispersão usa observado em X e estimado em Y; a diagonal representa concordância perfeita. Histograma e diagnósticos relativos ajudam a ver assimetria e viés; a faixa exibida no histograma não contém necessariamente todos os erros.
### Exemplo
Dois modelos com RMSE semelhante podem errar em idades diferentes. Examine resíduos por grupo e faixa de resposta. Se os mesmos dados foram usados no ajuste, o resultado descreve aderência e não desempenho externo garantido.

## Isolation Forest
### Finalidade
Detecta observações que são isoladas rapidamente por árvores aleatórias. O modelo usa todas as variáveis numéricas selecionadas; o gráfico mostra apenas uma projeção em dois eixos.
### Parâmetros
Configure quantidade de árvores, amostras por árvore, profundidade, número de variáveis sorteadas, limite de treinamento e semente. A contaminação define uma referência de quantil; um limiar manual de escore, quando informado, substitui essa escolha.
### Como usar
Selecione medidas como DAP, altura, idade e volume, evitando códigos usados apenas como identidade. Treine o modelo, confira os escores e, se necessário, aplique um modelo salvo a outra tabela com colunas compatíveis.
### Resultados
O relatório mantém as linhas e adiciona IF_Score e IF_Classificacao. Linhas incompletas ficam sem escore. O escore varia de 0 a 1 e não é probabilidade de erro de medição. Empates no limiar não são classificados como anomalia.
### Cuidados
Uma árvore rara pode ser biologicamente válida. Confirme a origem antes de retirar observações. O gráfico derivado de observações normais deve ser interpretado junto ao critério que produziu a classificação.

## K-means
### Finalidade
Agrupa observações numericamente semelhantes. Selecione duas ou mais variáveis quantitativas e escolha os eixos usados para visualizar os grupos.
### Regra técnica
As variáveis são padronizadas por z-score antes das distâncias. A inicialização k-means++ e as repetições reduzem a dependência de um centro inicial. O agrupamento procura reduzir as distâncias quadráticas dos pontos aos seus centros.
### Escolha de K
Compare inertia, mostrada pela curva elbow, e silhouette. Inertia tende a cair com mais grupos; procure o ponto em que o ganho marginal diminui. Silhouette compara coesão com separação, mas não substitui conhecimento do domínio. É possível escolher K final em vez do automático.
### Limites e saída
O limite de treinamento usa uma amostra determinística quando necessário; as linhas completas recebem grupo posteriormente. A silhouette pode ser estimada em amostra estratificada. Identificadores numéricos ou variáveis redundantes podem dominar a separação. Os grupos são descrições estatísticas, não classes biológicas automaticamente validadas.

# Catálogos de equações
## Volumetria
### Cadastro
Cadastre ID, nome, família, coeficientes e tipo de volume, como VTCC ou VCCC. Confira a fórmula e cada entrada: origem, coluna, unidade e fator. As opções do catálogo determinam como o Processador obtém o volume.
### Regra técnica
Equações legadas usam a forma Schumacher–Hall quando essa é a definição registrada: `V = exp(b0) × DAP^b1 × H^b2`. A expressão efetiva do cadastro e suas unidades prevalecem. Um coeficiente ajustado com DAP em centímetros não deve receber milímetros diretamente.
### Como usar
Defina os modelos, confira os códigos que relacionam os dados às equações e avalie exemplos conhecidos. Em Regras de volumetria das Configurações, revise o limite de idade da equação geral quando aplicável.
### Conferência
Não misture volumes com e sem casca na mesma interpretação. Confira a unidade da saída, a escala de aplicação por árvore e as agregações por hectare. Equações ajustadas para determinada espécie ou intervalo de tamanho podem falhar fora desse domínio.

## Crescimento e produção
### Cadastro e vigência
Cadastre curvas com ID, família, fórmula, coeficientes, ano e versão vigente. A definição preservada por versão permite comparar curvas anteriores com as atuais e identificar qual formulação foi aplicada.
### Entradas
Mapeie idade e demais preditores, com origem, coluna, unidade, fator e agregação por parcela quando aplicável. Uma equação com várias entradas precisa de todas elas válidas; o nome do modelo não substitui o mapeamento.
### Aplicações
As curvas alimentam comparações, ICA/IMA e projeções do Ativo biológico. Nas projeções proporcionais, o denominador avaliado na condição inicial precisa ser válido e diferente de zero.
### Exemplo
Uma curva cuja idade é medida em meses exige conversão correta quando a análise é pedida em anos. Confira a vigência por ID e a faixa de calibração antes de projetar muitos anos além das medições disponíveis.

## Índice de sítio
### Objetivo
Organiza modelos para relacionar idade, altura e condição de sítio. Cadastre as famílias e entradas e confira a unidade de idade e altura antes de usar equações-guia em gráficos ou aplicações.
### Parâmetros de interpretação
A idade-base fornece a referência comum para comparar sítios. Nos gráficos que usam a equação-guia vigente, selecione a curva, informe a idade-base em anos e configure o desvio máximo admitido nessa referência, quando essa opção for usada.
### Como usar
Cadastre ou registre o modelo ajustado, confirme o ID e sua vigência e verifique algumas observações conhecidas. Compare classes somente depois de conferir a equivalência das unidades e do conceito de altura usado.
### Cuidados
Altura dominante e altura média são indicadores diferentes. O fato de uma coluna se chamar H não determina seu significado. Classificação de sítio depende do modelo, da idade-base e da qualidade das observações; não atribua equivalência entre classes de modelos diferentes sem verificar suas definições.

## Regressões salvas e famílias de modelos
### Dois cadastros
Famílias descrevem a forma da equação e seus parâmetros. Regressões ajustadas salvas guardam coeficientes de uma execução concreta, com origem e configuração. Escolher uma família não equivale a selecionar um ajuste já calibrado.
### Como usar
Nas Configurações, revise as famílias disponíveis no editor de modelos de regressão. Após ajustar e conferir a convergência, salve a regressão e aplique-a pela ferramenta própria ou registre-a em um catálogo compatível.
### Cuidados técnicos
Coeficientes redundantes ou uma base sem variação suficiente podem tornar o ajuste não identificável. Mais coeficientes podem diminuir o erro de treinamento sem melhorar a previsão. Guarde unidades, domínio e significado de cada variável junto à equação.

# Planejamento de campo e GIS
## GIS: mapa e camadas
### Entradas
O mapa lê shapefiles e rasteres GeoTIFF do projeto. Um shapefile usa o conjunto de arquivos SHP, SHX, DBF e PRJ; o DBF guarda os atributos. O PRJ descreve o sistema de coordenadas. Um TIFF sem georreferenciamento adequado pode não ter posição válida no mapa.
### Navegação e visibilidade
Adicione camadas em Conteúdos, arraste para mover e use a roda para aproximar ou afastar. O checkbox da camada controla sua exibição. Duplo clique em um vetor ou raster abre o mapa e enquadra sua extensão. O mapa base usa OpenStreetMap e pode precisar de rede para carregar regiões ainda não disponíveis no cache.
### Projeções
O leitor tem suporte aos sistemas reconhecidos pelo aplicativo, incluindo geográfico, Web Mercator e Transversa de Mercator/UTM. Projeção desconhecida é informada; ausência de PRJ pode levar à interpretação como longitude/latitude. Para amostragem, use explicitamente coordenadas projetadas em metros.
### Interpretação
O mapa é uma visualização. Medidas de área e distância do planejamento são calculadas no sistema projetado da origem. A área aparente na tela do mapa base não deve substituir os cálculos em uma projeção adequada à região.

## Amostragem
### Entrada e identificação
Abra Ferramentas → Amostragem e escolha o shapefile de polígonos dos talhões. Campo identificador e Campo de projeto são seletores carregados da tabela de atributos desse arquivo. Procurar shapefile inclui uma origem externa no combo; trocar o arquivo atualiza os campos.
### Intensidade e borda
Hectares por parcela define a intensidade. As faixas de área e seus mínimos determinam a quantidade por talhão. Nos talhões da faixa maior, usa-se `teto(área_ha / hectares_por_parcela)`. Recuo negativo da borda restringe a área elegível; quando o buffer elimina o polígono, o fluxo conserva o original como fallback.
### Métodos
A sistemática atual usa centros ou vértices da malha em D1, a malha complementar em D2, fragmentos disponíveis em D3 e um ponto interno em D4 para talhões ainda vazios. D1 conserva os pontos válidos da malha e pode ultrapassar a quantidade teórica de um talhão. A sistemática com início aleatório desloca a origem da malha de forma reproduzível.
Distância máxima, ou maximin, começa por um candidato aleatório e escolhe candidatos afastados dos já selecionados. É uma heurística de cobertura espacial; sua saída não deve receber interpretação de probabilidades GRTS.
### Saída
Gerar no mapa salva uma nova camada em Conteúdos → Dados vetoriais. Os pontos usam azul, laranja, roxo e vermelho conforme a rodada; no GRTS, Base é azul e Reserva é laranja. O checkbox controla a camada. A exportação está no botão direito sobre a amostra e copia SHP, arquivos auxiliares, CSV, SVG e o registro de desenho `.design.json`.
### Numeração
A numeração por lote considera os primeiros 14 caracteres do identificador, com separação opcional por projeto. A ordem interna pode ser X/Y ou rodada. O número exibido no resultado é uma identidade operacional; a ordenação dos números não muda o sorteio que escolheu os pontos.

## GRTS: desenho e parâmetros
### Objetivo
Seleciona uma amostra espacialmente distribuída por endereços hierárquicos randomizados de quadrantes e amostragem sistemática na linha de probabilidades. Polígonos são aproximados por candidatos em grade hexagonal. A semente reproduz o resultado com a mesma origem e configuração.
### Estratos e quantidade
Escolha cada talhão, a área inteira ou um campo de atributos para formar estratos. Com campo ou área inteira, a quantidade é garantida por estrato, não por cada talhão que ele contém. A quantidade pode vir da intensidade, de um número fixo, da tabela por estrato ou do planejamento normal com confiança, erro relativo e CV.
O planejamento usa `n = teto((z × CV / erro)²)` e mínimo de duas parcelas por estrato. CV deve vir de informação anterior ou piloto; a precisão alcançada só pode ser avaliada com medições. Não se usa o número artificial de candidatos como tamanho finito da população para reduzir essa quantidade.
### Probabilidades
Igual por unidade de área dá a mesma intensidade à área elegível. Proporcional usa um campo numérico positivo como multiplicador por unidade de área. Por categoria usa o campo categórico e a quantidade esperada de parcelas em cada estrato e categoria; as quantidades devem ser positivas e somar o tamanho de base do estrato. São expectativas, não cotas fixas em cada realização.
### Candidatos, distância e reservas
Densidade controla candidatos por parcela e custo de execução. Distância mínima e tentativas procuram uma configuração com o espaçamento pedido no conjunto Base + Reserva dentro de cada estrato. Se o limite não for alcançado, o resultado conserva a quantidade e informa a distância obtida. Substitutas usam ordem hierárquica reversa e recebem ordem de uso própria.
### Pesos e limites
PI_APROX e PESO descrevem o quadro finito de candidatos; PESO_HA representa expansão em hectares da área elegível. Com ajuste de distância ou reservas, esses valores são nominais do desenho de base e PI_EXATA fica falso. Uma reserva não é uma observação adicional coletada: só entra na análise segundo o protocolo de substituição.
A ferramenta seleciona locais e registra o desenho. Ainda não calcula média de volume, intervalo de confiança do inventário, variância espacial ou ajuste por não resposta. Não inclui parcelas históricas obrigatórias. Confira `.design.json` para semente, parâmetros, tamanhos por estrato, tentativas, distância obtida e avisos.

# Configurações do projeto
## Configurações: aplicar e salvar
### Onde ficam
Abra Projeto → Configurações. As abas reúnem mapeamentos, cadastro, altura, processamento, estatística e saída, além dos editores de identificação, colunas do resumo direto, regras de volumetria e famílias de regressão.
### Como usar
Edite os parâmetros e use Aplicar configurações. A validação confere a estrutura e as regras; se aparecer um erro, revise o campo indicado. Salve o projeto para reutilizar as escolhas. Fechar o diálogo sem aplicar não publica as alterações da cópia em edição.
### Alcance
Configurações novas valem para operações futuras. Tabelas e versões já calculadas não são recalculadas apenas por mudar um mapeamento. O catálogo de Volumetria, o de Crescimento e produção e o de Índice de sítio têm telas próprias de cadastro.
### Roteiro de conferência
Confirme primeiro origem, worksheet e cabeçalho; depois colunas e unidades; em seguida identificação, cadastro, altura e modelos. Por fim revise estatística e saída. Esse roteiro ajuda a distinguir erro de dado de erro de configuração.

## Configurações: inventário, colunas e unidades
### Mapeamento
Relacione os conceitos usados no cálculo aos nomes reais de colunas: talhão, parcela, lote, DAP, altura, data, dimensões da parcela e declinações. O nome apresentado no arquivo pode ser diferente do conceito interno.
### Unidades
Defina explicitamente DAP e altura. Dados em mm e dm são frequentes, mas as colunas podem estar em cm e m. A conversão do inventário e o fator de entrada de uma equação são etapas diferentes: revisar ambos evita conversão repetida.
### Área da parcela
Comprimento, largura e declinações participam da validação e conversão da área conforme o formato admitido. Configure o tratamento de declinações vazias; assumir zero precisa corresponder ao protocolo de campo, pois altera a área horizontal e as expansões por hectare.
### Exemplo e diagnóstico
Um DAP registrado como 200 mm corresponde a 20 cm. Interpretá-lo como 200 cm multiplica a área seccional por 100. Se uma parcela inteira tem todos os indicadores muito altos, confira a área e as unidades antes de investigar árvores individualmente.

## Configurações: cadastro florestal
### Entradas
Selecione o arquivo do cadastro, a worksheet, sua chave e as colunas de data de plantio e área efetiva. A chave do cadastro pode ter nome diferente da chave do inventário, mas precisa representar a mesma unidade.
### Uso técnico
A data de plantio permite calcular idade em relação à medição. A área efetiva participa das consolidações territoriais. Um cadastro repetido para a mesma chave pode tornar a ligação ambígua; confira as duplicatas antes de processar.
### Idade por informação alternativa
Quando a configuração permitir fallback de idade a partir de um código ou padrão anual, revise o padrão e sua interpretação. Essa regra é uma alternativa controlada à data cadastral; não substitui a confirmação das datas reais de plantio.
### Exemplo
Se o inventário usa o código de talhão e o cadastro usa código de lote, o vínculo pode agregar áreas incorretamente. Compare algumas chaves e seus atributos manualmente, inclusive casos sem correspondência, antes da execução em lote.

## Configurações: identificação
### Objetivo
Define quais colunas distinguem árvore, parcela, talhão e base. Identidade correta impede unir registros diferentes e fornece o vínculo para processos como pareamento temporal, revisão e auditoria.
### Como configurar
Escolha uma tabela para consultar seus campos e marque as colunas de cada chave. As colunas da base e o tipo de inventário acompanham as identidades. Fonte é uma opção de origem criada na junção, mas outra combinação pode ser mais adequada.
### IFC, IFPC e MISTO
A coluna configurada para tipo tem prioridade. No automático, o sistema procura campos conhecidos e os nomes de Fonte; quando isso não identifica o tipo, usa a escolha do Processador. Em modo misto, a origem precisa distinguir o tipo de cada linha.
### Exemplo
Árvore 1 da Parcela 1 do Talhão A é diferente da Árvore 1 do Talhão B. Em remedições, a data distingue ocasiões de medição; no pareamento, ela sai da chave que busca a mesma unidade ao longo dos anos. Decida também se Fonte representa uma população diferente ou apenas uma campanha posterior.

## Configurações: altura e modelos
### Modos
KEEP_FILE conserva a altura do arquivo. MODEL_GENERAL usa o modelo geral selecionado. FINETUNE_MODEL ajusta o uso do modelo com dados locais segundo os parâmetros de calibração admitidos. O modo legado CALIBRATE é recusado pelo processamento atual; não deve ser tratado como equivalente ao finetune.
### Modelo e variáveis
Selecione o pacote do modelo e confira as entradas previstas nos metadados. Para um modelo pré-treinado, os nomes e a ordem efetivos das entradas precisam seguir o pacote. Não reordene livremente preditores esperando o mesmo resultado.
### Variáveis auxiliares
Variáveis customizadas podem derivar fórmulas, diferenças entre datas ou colunas do cadastro. Estatísticas por parcela têm nome, coluna, função, fórmula e filtro. Essas expressões usam o parser do aplicativo, não execução de scripts arbitrários.
### Finetune e diagnóstico
Os mínimos de amostras por etapa controlam quando aplicar correções de viés, ajuste linear ou calibração com regularização Ridge. Ridge alpha controla a penalização. Amostras pequenas não sustentam correções complexas. Debug habilita campos intermediários e saídas de conferência; não significa que todo valor intermediário seja resultado final de inventário.

## Configurações: processamento e duplicatas
### Regras
Confira o tipo de processamento, o tratamento de árvores duplicadas, parâmetros de consolidação e as opções específicas do modo escolhido. Identificação e remoção de duplicatas são decisões ligadas: duplicata é avaliada pela chave configurada.
### Ordem do cálculo
Quando habilitada, a remoção de árvores duplicadas ocorre antes do ajuste de altura e das somas. O arquivo de entrada não é reescrito por essa etapa. Uma chave incompleta pode retirar árvores distintas; uma chave detalhada demais pode deixar duplicatas passarem.
### Conferência
Compare contagens antes e depois e confira exemplos de chaves repetidas. No fluxo versionado, a revisão usa o processamento original e os fatores publicados. No resumo direto, o fator opcional é aplicado por árvore antes das somas, conforme esse modo.
### Exemplo
Se Cova identifica a posição e Fuste distingue brotações, omitir Fuste pode fazer dois indivíduos válidos parecerem repetidos. Verifique as colunas que efetivamente identificam a sua unidade de campo.

## Configurações: estatística do inventário
### Parâmetros
Nível de confiança controla o quantil usado nos intervalos admitidos pelo relatório. Mínimo de parcelas condiciona o cálculo de dispersão e erro. Erro máximo de alerta destaca resultados para revisão, sem mudar as medições.
### Idade e tempo
A chave temporal ativa separa remedições nos agrupamentos pertinentes. Sem separação adequada, medições de épocas diferentes podem aparecer na mesma consolidação. Confirme a unidade temporal e a coluna de data antes de comparar campanhas.
### Interpretação
Intervalos usam as unidades amostrais e as regras do relatório, não uma certificação automática de independência. Muitas árvores na mesma parcela não equivalem a muitas parcelas independentes. Não troque o nível de confiança por um limiar de aprovação de qualidade de campo.
### GRTS e relatórios
As opções estatísticas do processamento não implementam automaticamente a variância de um desenho GRTS. O dimensionamento por CV do planejamento e a estimação estatística após a coleta são etapas diferentes. Preserve o desenho amostral junto das medições para a análise adequada.

## Configurações: regras de volumetria e comparação IFC
### Volumetria
Regras de volumetria reúne opções que complementam as equações, como o limite de idade da equação geral. Os coeficientes, entradas e tipos de volume são cadastrados no catálogo próprio. Confira a faixa de idade e o código de aplicação antes de mudar uma regra geral.
### Comparação IFC
Quando habilitada e aplicável ao resultado, a comparação utiliza parâmetros e limiares para classificação e formatação. Os limiares verde e amarelo orientam destaque; não alteram a equação volumétrica por si só.
### Leitura dos resultados
Uma cor é um sinal de enquadramento na regra configurada, não uma prova de erro nem uma aprovação técnica automática. Verifique o referencial comparado, a definição do indicador e a unidade antes de mudar o limiar para reduzir alertas.
### Rastreabilidade
Registre a mudança de parâmetros e reexecute o fluxo apropriado quando precisar de novo resultado. Resultados antigos permanecem associados ao contexto em que foram calculados.

## Configurações: colunas do resumo direto
### Objetivo
Controla as colunas opcionais por nível: árvore, parcela, talhão, lote e projeto. As caixas obrigatórias ficam protegidas porque sustentam identificação, rastreabilidade ou indicadores necessários à auditoria.
### Como usar
Abra a aba correspondente e marque os campos desejados ou use Habilitar opcionais e Desabilitar opcionais. Aplique, salve o projeto e habilite os níveis de saída no Processador.
### Efeito técnico
Desativar uma coluna reduz a apresentação daquele resultado. Não muda os cálculos internos, as variáveis usadas no modelo de altura nem a calibração. A seleção vale para novos processamentos; tabelas anteriores são preservadas.
### Exemplo
É possível omitir um indicador auxiliar da tabela por árvore e mantê-lo no resumo por parcela. Ao preparar um relatório para terceiros, preserve os campos essenciais para conferir unidade, área e origem do resultado.

## Configurações: saída e exportação
### Opções
O sufixo do arquivo e a seleção de abas controlam nomes e relatórios exportados. Abas auxiliares, informações, modelos aplicados e cálculos dependem das opções correspondentes; saídas de debug exigem esse modo habilitado.
### Fluxos diferentes
Tabelas de trabalho ficam no projeto e podem ser exportadas individualmente. O inventário versionado tem regras próprias para exportar a versão aprovada vigente. Na amostragem, a exportação está no botão direito da camada e inclui o registro do desenho.
### Conferência
Confirme qual versão, tabela e seleção de abas estão sendo entregues. Habilitar uma aba não cria retroativamente um resultado que não foi calculado. Excel e CSV são saídas para intercâmbio; o projeto preserva a organização de trabalho e os históricos pertinentes.

## Configurações: modelos de regressão
### Finalidade
O editor define as famílias matemáticas disponíveis para os ajustes. Uma família reúne fórmula, coeficientes e preditores; o ajuste posterior estima valores para esses coeficientes com uma base de dados.
### Como configurar
Escolha uma família compatível com a pergunta, confira quantidade e significado das variáveis e revise domínio e nomes dos coeficientes. Aplique e salve para reutilizar o catálogo no projeto.
### Cuidados técnicos
Evite expressões com coeficientes que fazem exatamente o mesmo papel: isso pode impedir identificação e testes de modelos. Fórmulas precisam produzir valores finitos para as entradas válidas da análise. Uma fórmula estatisticamente ajustável ainda pode ser biologicamente inadequada fora da faixa observada.
### Uso posterior
Regressões salvas após o ajuste são aplicações concretas dessas famílias. Antes de usá-las no processamento ou em projeções, confira o mapeamento das variáveis, as unidades e a origem dos dados de calibração.
