# Cálculos integrados às ferramentas
## Roteiro de trabalho
### Notação para ler as contas
Nos exemplos, n é a quantidade de unidades válidas, xᵢ é a observação i, x̄ é a média, s é o desvio-padrão amostral, ŷᵢ é a previsão e Σ indica soma. “Por hectare” é uma taxa; “total” é essa taxa expandida por uma área. DAP, altura, idade e área precisam estar na unidade da fórmula antes de qualquer operação.
> Média: x̄ = Σxᵢ / n
> Variância amostral: s² = Σ(xᵢ − x̄)² / (n − 1)
> Desvio-padrão: s = √s²; erro-padrão da média: EP = s / √n
**Desvio-padrão** descreve dispersão entre observações. **Erro-padrão** descreve a precisão da média sob o modelo amostral adotado. **RMSE** mede discrepância entre observado e previsto. Os três podem ter a mesma unidade física, mas respondem a perguntas diferentes.
### Unidade amostral e dependência
Dez árvores em cada uma de três parcelas não são automaticamente trinta parcelas independentes. O n usado na incerteza deve corresponder às unidades e ao procedimento de estimação. Uma tabela maior não garante informação independente maior quando existem remedições, agrupamentos ou duplicatas.
### Como ler os exemplos
Os números arredondados neste manual são didáticos. O aplicativo usa os valores disponíveis na operação e só depois formata os resultados. “Valor indefinido” não deve ser substituído por zero: média zero, variância nula, denominador inválido e ausência de dados são situações distintas.

## Processador
### Da árvore à parcela
Com diâmetro d em cm e altura h em m, a área seccional g é a área de um círculo. A área basal amostrada é a soma dos g dos fustes válidos; a área basal por hectare é essa soma expandida pela área horizontal da parcela a, em m².
> gᵢ = πdᵢ² / 40000, em m²
> G_amostrada = Σgᵢ; G_ha = G_amostrada × 10000 / a
> V_parcela = Σvᵢ; V_ha = V_parcela × 10000 / a
> N_ha = N_fustes × 10000 / a; VMI = V_ha / N_ha
Exemplo: uma parcela horizontal de 400 m² com 40 fustes e volume amostrado de 8 m³ tem fator de expansão 25, densidade de 1000 fustes/ha, volume de 200 m³/ha e VMI de 0,20 m³/fuste. VMI não é a média dos volumes por hectare; é volume médio individual segundo os indicadores usados.
### Média de diâmetros e diâmetro quadrático
> DAP_médio = Σdᵢ / n_d; Dq = √(Σdᵢ² / n_d)
Para d = 10 e 30 cm, a média é 20 cm e Dq ≈ 22,36 cm. Dq dá mais peso aos diâmetros grandes por usar quadrados. DAPd é outro indicador: a média dos maiores DAP segundo a quantidade configurada. Hd usa as alturas válidas das mesmas árvores selecionadas por DAP, não as maiores alturas de uma seleção independente.
### Expansão por talhão e por área
O resumo direto calcula a média das taxas por parcela dentro do talhão. Para níveis superiores, estima o total de cada talhão por sua área efetiva e divide a soma dos totais pela soma das áreas. A área efetiva é contabilizada por talhão, não repetida para cada árvore.
> V_total = Σ(Aₕ × V̄_ha,h); V̄_ha,conjunto = V_total / ΣAₕ
Talhões de 10 e 20 ha, com médias 200 e 300 m³/ha, produzem 2000 + 6000 = 8000 m³ e média de 266,67 m³/ha. A média simples das duas taxas seria 250 e responderia a outra ponderação. As linhas de parcela do resumo direto não recebem, como se fossem seus próprios totais, a expansão do talhão inteiro.
### Área horizontal e dados ausentes
A rotina atual de área usa `a = (L/10) cos(θL) × (W/10) cos(θW)`, com os ângulos em graus convertidos a radianos. A divisão por 10 faz parte da convenção atual das dimensões da entrada. Uma dimensão 200 corresponde a 20 m nessa conta; confira o formato antes de usar dimensões registradas diretamente em metros.
Uma parcela de 20 × 20 m com declinação de 30° somente no comprimento tem área horizontal de aproximadamente 346,41 m². O mesmo volume de 8 m³ passa a 230,94 m³/ha. Área inválida interrompe a operação com diagnóstico; ausência de altura ou volume precisa ser conferida nos contadores pertinentes, sem assumir que todos os fustes receberam previsão válida.

## Revisão de inventário
### Conta do fator
> vᵢ,revisão = f_grupo × vᵢ,original
O grupo é definido pelo nível do fator. As somas por parcela e as taxas por hectare são refeitas a partir desses volumes. Um fator único 0,90 transforma 200 em 180 m³/ha quando os demais elementos da parcela são mantidos.
Revisões usam o volume original de referência. Se uma revisão usa 0,90 e a seguinte usa 0,80, o novo resultado para uma referência 200 é 160, e não 144. O produto 0,90 × 0,80 só faria sentido se o procedimento pedisse fatores cumulativos, que não é a regra deste fluxo.
### Reflexos estatísticos
Se todas as observações de um grupo forem multiplicadas pelo mesmo f positivo, média e desvio-padrão são multiplicados por f e a variância por f². O CV e o erro relativo permanecem iguais nesse caso ideal. Com fatores diferentes entre parcelas, essas relações simples não valem para o conjunto: é preciso recalcular a dispersão.
O fator não cria parcelas novas, não corrige a independência entre elas e não repete a calibração de altura. O registro da versão e do motivo permite distinguir revisão de volume de alteração da própria base.

## Auditoria pós-processamento
### Quartis, faixa de idade e alertas
O grupo de referência combina as colunas escolhidas e a faixa etária obtida pela parte inteira de idade/largura_da_faixa. Em cada grupo, os volumes válidos e não negativos são ordenados para calcular mediana, Q1 e Q3.
> IQR = Q3 − Q1; limite_superior = Q3 + k × IQR
Se Q1 = 180, Q3 = 220 e k = 1,5, o limite é 280 m³/ha. Um valor 300 fica sinalizado; isso não prova que esteja errado. O limiar só é usado quando há o mínimo configurado de parcelas no grupo. Uma largura de faixa de 2 anos coloca uma idade de 7,8 na faixa de índice 3, e não numa vizinhança contínua centrada em 7,8.
### Identidades aritméticas
> Volume_recomposto = N_ha × VMI; diferença_relativa = 100 × (valor − referência) / referência
Exemplo: 1000 fustes/ha e VMI de 0,20 m³/fuste recompõem 200 m³/ha. Diferenças além da tolerância numérica podem indicar campo incompatível, unidade ou regra de cálculo diferente. Referência nula não permite interpretar essa razão como percentual finito.
### Evolução temporal
> ΔV = V_atual − V_anterior; incremento_médio_do_intervalo = ΔV / Δidade
Uma passagem de 200 para 230 m³/ha em dois anos dá 15 m³/ha/ano no intervalo. Uma queda pode refletir mortalidade, desbaste ou mudança de população, e não apenas erro. O relatório precisa ser lido com as identidades e a continuidade das medições. A distribuição do grupo de referência inclui a própria parcela e não é um teste independente de significância de cada alerta.

## Juntar tabelas
### A operação em termos de conjuntos
A junção desta ferramenta é uma concatenação vertical. Se as origens A e B têm n_A e n_B linhas, o resultado deve ter n_A + n_B linhas, salvo filtros ou regras explicitamente escolhidos. Não há combinação de cada linha de A com cada linha de B por uma chave.
Na opção de colunas comuns, o conjunto de cabeçalhos de saída é a interseção dos cabeçalhos. Assim, A com campos {DAP, H} e B com {DAP, H, Idade} gera apenas DAP e H nessa opção.
### Efeito nas estatísticas
Concatenar médias já calculadas é diferente de reunir observações. Para grupos da mesma população, a média conjunta das observações é `(n_A x̄_A + n_B x̄_B)/(n_A+n_B)`, e não necessariamente `(x̄_A+x̄_B)/2`. A ferramenta não decide se essas populações podem ser reunidas; Fonte permite rastrear essa decisão depois.

## Parear medições
### Unidade, agregação e diferenças
Primeiro se forma a identidade sem a data; depois são organizadas as ocasiões de cada unidade. Se houver mais de um valor da mesma unidade na mesma ocasião, a regra de agregação define o valor que será pareado. Média usa soma/quantidade, enquanto soma preserva o total daquele grupo; as escolhas respondem a perguntas diferentes.
> Δy = y₂ − y₁; Δy_anual = (y₂ − y₁)/(t₂ − t₁)
Calcule a taxa somente com intervalo positivo e na unidade temporal correta. Alturas 18 e 21 m em dois anos correspondem a diferença de 3 m e taxa média de 1,5 m/ano; diferença de duas datas não é necessariamente um ano.
### Estatística posterior
O teste t pareado trabalha com as diferenças entre pares, não com duas médias independentes. Para isso, cada linha deve representar um par correto. O pareamento organiza os dados; não realiza automaticamente um teste, não garante independência entre pares e não corrige códigos de árvore trocados em campo.

## Gerar coluna
### Ordem das operações
Uma expressão é avaliada nas linhas da tabela. Identifique numerador, denominador e unidade do resultado antes de gravar. Parênteses tornam explícita a precedência; `[Volume]/[Área]*10000` calcula taxa por hectare somente se Volume estiver em m³ e Área em m².
### Razões e agregações
> Média_das_razões = (Σ vᵢ/aᵢ)/n
> Razão_das_somas = Σvᵢ / Σaᵢ
Esses valores não são iguais em geral. Para parcelas com volumes 4 e 16 m³ e áreas 200 e 800 m², ambos dão 200 m³/ha; mas, se o segundo volume for 24, a média das taxas será 250 e a razão das somas será 280 m³/ha. Escolha a expressão segundo o estimador e o protocolo desejados.
Dividir por zero, usar texto como número ou aplicar logaritmo fora de seu domínio pode deixar um resultado inválido. Confira esses casos na tabela, não só nas primeiras linhas da prévia.

## Estratificar
### O que a separação conserva
Quando os grupos são mutuamente exclusivos e todos são selecionados, a soma das contagens dos estratos deve ser a contagem de linhas elegíveis após os filtros. Se um mesmo código aparece com grafias diferentes, passa a representar grupos diferentes.
### Médias estratificadas
> μ̂ = Σ Wₕ × ȳₕ; ΣWₕ = 1
Wₕ pode representar a proporção da área de cada estrato, conforme o desenho; não deve ser escolhido automaticamente pela quantidade de linhas disponível. Grupos de 10 e 30 ha com médias de 100 e 200 m³/ha dão média por área de 175 m³/ha.
Esta ferramenta separa tabelas. Ela não estima automaticamente Wₕ, a variância estratificada ou um intervalo de confiança para o inventário. Esses cálculos exigem desenho, pesos e unidades amostrais apropriados.

## Limpeza integrada
### O efeito da remoção nas contas
Ao retirar as linhas selecionadas, todas as análises da sessão passam a usar o subconjunto restante. Média, variância, correlação e ajuste podem mudar mesmo que se retire apenas uma observação extrema.
> Média_após_retirar_x = (n × x̄ − x)/(n − 1)
Em {10, 10, 40}, a média é 20. Retirar 40 produz média 10. Essa mudança pode ser uma correção válida se 40 era erro de entrada; pode ser um viés de seleção se 40 era uma árvore real da população de interesse.
### Limite estatístico
Remover pontos porque o ajuste melhora usa a própria resposta para selecionar a amostra. As métricas posteriores descrevem a base modificada. Preserve a origem, o critério, as linhas retiradas e a justificativa para que outra pessoa possa reproduzir a análise.

## Ativo biológico
### Idade e projeção proporcional
A idade em anos é calculada a partir da diferença entre datas, com dias divididos por 365,25 na rotina atual. A curva é avaliada em sua unidade de idade e com o fator registrado.
> Y₂ = Y₁ × F(I₂)/F(I₁)
Se Y₁ = 200 m³/ha, F(I₁) = 250 e F(I₂) = 300, a projeção é 240 m³/ha. O quociente das avaliações é adimensional; a unidade da projeção permanece a de Y₁. O resultado passa pelo valor observado inicial mesmo que F(I₁) não seja igual a Y₁.
### Incremento e base observada
> IMA(t) = Y(t)/t
A ferramenta busca a medição mais recente por chave; ela não tira a média de todas as campanhas para formar Y₁. Nas projeções anuais, I₂ corresponde à idade em 1º de junho do ano de referência; para IMA por idade, avaliam-se idades inteiras.
F(I₁) nulo, negativo ou não finito compromete a razão. Previsões de volume não são avaliações monetárias, e a ferramenta não adiciona intervalo de predição ao redor de cada projeção.

## Gráficos
### Quantis e violinos
Um violino descreve a distribuição de Y dentro de uma faixa de X. A suavização combina kernels gaussianos: cada observação contribui mais perto de seu Y e menos longe. O aplicativo usa essa densidade como silhueta normalizada, não como contagem de árvores em cada largura.
> Silhueta(y) ∝ Σ exp(−0,5 × ((y − yᵢ)/h)²)
h é a largura de banda; aumentar h suaviza detalhes e pode unir picos. A resolução define quantos pontos avaliam o desenho, e não quantas observações existem na base. Mediana e quartis continuam sendo resumos dos valores do grupo.
### Escala e seleção
Alterar os limites do eixo muda o enquadramento visual, não a unidade dos dados. Dois gráficos com amplitudes diferentes não permitem comparar inclinações apenas pelo ângulo da linha. Estratos e filtros definem quais observações participam do gráfico e do ajuste associado; confira a quantidade válida quando houver valores ausentes.

## Ajuste de regressão
### Função objetivo e resíduos
No ajuste de mínimos quadrados, os coeficientes β são procurados para reduzir a soma dos resíduos quadráticos. A rotina usa `rᵢ = yᵢ − f(xᵢ; β)` nessa minimização; a validação descritiva apresenta o erro com o sinal oposto, estimado − observado. A soma dos quadrados não muda com a troca de sinal.
> SQres(β) = Σ[yᵢ − f(xᵢ; β)]²
> Linear: ŷ = β₀ + β₁x; β̂₁ = Σ[(xᵢ − x̄)(yᵢ − ȳ)] / Σ(xᵢ − x̄)²; β̂₀ = ȳ − β̂₁x̄
O exemplo linear explica a estimativa por mínimos quadrados; no programa, a rotina aplicada depende da família. Polinômios reconhecidos usam uma estratégia numérica própria, com transformação de escala para estabilidade. Famílias gerais usam otimização iterativa com diferentes inicializações.
### Passo do ajuste não linear
O Jacobiano J contém a sensibilidade da previsão a cada coeficiente. Na rotina geral, essas derivadas são aproximadas por diferenças centrais, com passo `10⁻⁵ × máximo(1, |βⱼ|)`.
> [JᵀJ + λD] Δ = Jᵀr; β_novo = β + Δ
D usa na diagonal o máximo entre a diagonal de JᵀJ e 1. Quando o passo reduz SQres, a rotina aceita e reduz λ; em passos rejeitados, aumenta λ. Ela verifica gradiente, tamanho do passo e melhoria do objetivo, com limite de iterações. Convergência numérica não prova que o mínimo global foi encontrado.
### Métricas e penalização de complexidade
> RMSE_ajuste = √(SQres/n); R² = 1 − SQres/SQtotal
> R²_ajustado = 1 − (SQres/SQtotal) × (n − 1)/(n − k)
> AIC_exibido = n ln(SQres/n) + 2k; BIC_exibido = n ln(SQres/n) + k ln(n)
k é a quantidade de coeficientes usada pela rotina. AIC e BIC omitidos de constantes comuns são comparáveis apenas em ajustes compatíveis, com a mesma resposta e as mesmas observações. Essa implementação não deve ser chamada de AICc. O RMSE usa n; não confunda com uma estimativa residual que divide por n − k.
### Exemplo e interpretação
Com n = 20, k = 2, SQres = 100 e SQtotal = 500, RMSE = √5 ≈ 2,24, R² = 0,80 e R² ajustado ≈ 0,789. Acrescentar coeficientes costuma melhorar a aderência à calibração; use validação e plausibilidade para avaliar a utilidade real.

## Aplicar regressões
### Avaliação de uma equação
Depois do ajuste, os coeficientes são mantidos e cada linha fornece os preditores. Para `ŷ = 2 + 3x`, x = 4 gera ŷ = 14. Aplicar a equação não reestima 2 e 3 com a nova base.
### Projeção por razão
> Y₂ = Y₁ × f(X₂)/f(X₁)
Exemplo: Y₁ = 100, f(X₁) = 80 e f(X₂) = 120 produzem Y₂ = 150. É uma projeção relativa ancorada na condição inicial, diferente da predição direta f(X₂) = 120.
Se a equação possui vários preditores, X₁ e X₂ representam vetores completos de entradas nas duas ocasiões. Divisor zero e valores fora do domínio da fórmula são inválidos. A saída é uma previsão pontual; a ferramenta não calcula automaticamente um intervalo de predição com incerteza dos coeficientes e dos novos dados.

## Ajustar rede neural
### Como uma rede calcula
Cada camada transforma o vetor recebido numa combinação linear, soma o viés e aplica a função de ativação. Numa camada com dois neurônios, cada neurônio tem seu próprio conjunto de pesos.
> z⁽ˡ⁾ = W⁽ˡ⁾a⁽ˡ⁻¹⁾ + b⁽ˡ⁾; a⁽ˡ⁾ = φ(z⁽ˡ⁾)
> ReLU(z) = máximo(0,z); tanh(z) = (exp(z) − exp(−z))/(exp(z) + exp(−z))
O pacote preserva a ativação e as transformações de entrada/saída. Uma categoria codificada por one-hot vira indicadores 0/1; ela não recebe automaticamente uma ordem numérica, como espécie A = 1 e B = 2.
### Treinamento e escalas
No treinamento integrado de rede, as variáveis são padronizadas e a resposta também pode estar na escala padronizada da rotina. O treinamento minimiza MSE usando Adam; a previsão retorna à unidade original por transformação inversa do scaler guardado.
> MSE = Σ(ŷᵢ − yᵢ)²/n; x_padronizado = (x − média_treino)/desvio_treino
Uma época é uma passagem pelo conjunto de treinamento. O lote define quantos registros contribuem para cada atualização de pesos. Adam mantém médias móveis do gradiente e de seu quadrado para ajustar os passos. Parada antecipada recupera o estado admitido pelo fluxo quando o indicador monitorado deixa de melhorar.
### Validação
Sem separação, as métricas são de treinamento. Na divisão 80/20, o ajuste usa uma parte e a avaliação a outra. No K-Fold, repetem-se ajustes deixando uma partição de fora em cada rodada. Nenhum desses procedimentos identifica automaticamente dependência temporal ou espacial: a mesma parcela em treino e teste pode produzir avaliação otimista.

## Ajustar Random Forest
### Previsão por conjunto de árvores
Uma árvore divide o espaço das variáveis em regiões e prevê a média da resposta de treinamento na folha. Numa divisão para regressão quadrática, procura-se reduzir a soma dos quadrados dentro dos nós.
> ŷ_RF(x) = [T₁(x) + T₂(x) + ... + T_B(x)] / B
B é o número de árvores. A aleatoriedade de amostras e de variáveis reduz a semelhança entre árvores; a média estabiliza a previsão. O papel exato de max_features depende da biblioteca de treinamento: controla o conjunto de preditores considerado nas divisões, não a unidade física de cada variável.
### Complexidade e exemplo
Se três árvores preveem 100, 120 e 110 m³/ha, a média do conjunto é 110 m³/ha. A dispersão entre essas três previsões não é, por si só, um intervalo de confiança do inventário.
Árvores mais profundas criam regiões menores e podem memorizar particularidades. Em extrapolação, a estrutura por folhas não fornece automaticamente crescimento biológico suave. O modelo deve ser validado em dados apropriados, e os nomes e codificações das preditoras precisam coincidir na aplicação.

## Ajustar Gradient Boosting
### Correção sequencial dos erros
Para a perda quadrática usada pelo estimador configurado, o gradiente negativo é proporcional ao resíduo. Cada etapa ajusta uma árvore à estrutura que ainda falta explicar e acrescenta uma correção amortecida.
> Fₘ(x) = Fₘ₋₁(x) + η × γₘ × Tₘ(x)
η é a taxa de aprendizado, Tₘ é a árvore da etapa e γₘ representa a escala da correção daquela etapa. Na perda quadrática, folhas são ajustadas às diferenças residuais pertinentes. As árvores formam uma sequência, ao contrário da média de árvores da Random Forest.
### Exemplo
Se a previsão atual é 100, a correção estimada pela etapa é +20 e η = 0,1, a nova previsão simplificada passa a 102. Aumentar o número de etapas e diminuir η pode produzir outra trajetória de ajuste, não uma equivalência automática.
### Implicações estatísticas
Subsample menor que 1 usa somente uma fração dos registros em cada etapa. Taxa, profundidade e número de etapas interagem. Escolha os parâmetros pelo desempenho em dados separados, pois o erro de treinamento isolado favorece complexidade crescente. A ferramenta não fornece automaticamente intervalo de predição por ser um conjunto de árvores.

## Ajustar SVR
### Função de perda e regularização
A regressão ε-SVR procura uma função relativamente regular e admite uma faixa de tolerância ε ao redor da resposta usada no treinamento. Desvios dentro da faixa não recebem penalização dessa perda; fora dela, a penalização cresce.
> Lε(y, f(x)) = máximo(0, |y − f(x)| − ε)
> Objetivo conceitual = (1/2)‖w‖² + C × ΣLε(yᵢ, f(xᵢ))
C regula a importância de reduzir desvios em relação à regularidade da função. ε é expresso na escala da resposta entregue ao estimador. Se o treinamento transforma a resposta, 0,1 nessa escala não significa automaticamente 0,1 m³/ha nem 10%.
### Kernels e previsão
> f(x) = Σ(αᵢ − αᵢ*) K(xᵢ,x) + b
> K_linear(x,z) = xᵀz; K_RBF(x,z) = exp(−γ‖x − z‖²)
No kernel polinomial, o grau controla a potência da transformação. γ determina a extensão da influência no RBF conforme o estimador; padronização e encoder guardados no pacote fazem parte da função aplicada depois.
Exemplo: para ε = 2 na unidade de treinamento, um erro absoluto 1 tem perda zero, enquanto erro 5 tem perda 3. Isso descreve a função de treinamento, não uma garantia de que as previsões ficarão dentro da faixa em dados novos.

## Fronteira de eficiência
### Como as faixas são formadas
Na divisão por largura, a faixa depende da posição de X entre o mínimo e o máximo. Na divisão por quantidade, as observações ordenadas por X são repartidas por sua posição na lista. Quantidade de faixas e mínimo de observações controlam a resolução e a estabilidade do envelope.
### Percentil e média de cauda
Para n valores Y ordenados e percentil p entre 0 e 1, a rotina usa posição `(n−1)p` com índices iniciados em zero e interpola os vizinhos.
> Q(p) = yₗ + [(n−1)p − l] × (yᵤ − yₗ)
Para Y = {10,20,30,40}, p = 0,75 dá posição 2,25 e Q = 32,5. Para média da cauda superior de 25%, a quantidade é `teto(4 × 0,25) = 1`, portanto a média é 40. Percentil e média da cauda são métodos diferentes.
### Significado
A fronteira liga resumos de faixas e omite faixas insuficientes. Não é uma análise DEA nem uma fronteira estocástica com decomposição de erro. Por isso, a distância de um ponto ao envelope não é, automaticamente, uma estimativa de ineficiência de um processo produtivo.

## Comparar curvas de produção
### Diferença e tolerância prática
As funções são avaliadas em idades comuns e nas unidades definidas para comparação. A diferença absoluta é nova − base; a relativa usa a base como denominador quando ele é válido.
> Δ(t) = F_nova(t) − F_base(t)
> Fora_da_tolerância se |Δ(t)| > tolerância_abs + (tolerância_rel/100) × |F_base(t)|
Para F_base = 200, tolerância absoluta 5 e relativa 3%, o limite da diferença é 11 unidades. Uma diferença de 10 fica dentro; uma diferença de 12 fica fora. O limiar combina os dois termos por soma nesta implementação.
### Desempenho sobre a mesma base
Com observações, cada curva recebe erros previsto − observado e métricas como MAE, RMSE e viés. Para comparar desempenho, é essencial que ambas sejam avaliadas nas mesmas observações válidas.
### Intervalo por unidades independentes
A inferência disponível calcula, em cada unidade declarada independente, a média de `|erro_base| − |erro_nova|`. Depois atribui peso igual às unidades e forma um IC t aproximado de 95% sobre essas médias. Exige pelo menos cinco unidades válidas como mínimo operacional, além da declaração de validação independente e das condições da ferramenta.
> IC95%(redução) = d̄ ± t₀,₉₇₅;u₋₁ × s_d/√u
u é a quantidade de unidades independentes, não o número total de linhas. IC inteiramente positivo favorece a curva nova nessa base. Com diferença constante, o intervalo é degenerado e o programa não oferece a conclusão inferencial usual. Não há demonstração causal de ganho de produção.

## ICA e IMA
### Por que o cruzamento pode indicar culminação
> IMA(t) = V(t)/t; ICA_instantâneo(t) = V′(t)
> IMA′(t) = [tV′(t) − V(t)]/t²
Para t positivo, IMA′ = 0 exige V′(t) = V(t)/t. Portanto ICA = IMA é uma condição de ponto estacionário. Se a diferença ICA − IMA muda de positiva para negativa, a classificação é máximo local; a mudança oposta indica mínimo local.
### Aproximação numérica usada
> V′(t) ≈ [V(t−2h) − 8V(t−h) + 8V(t+h) − V(t+2h)]/(12h)
A rotina escolhe h pequeno em função da idade, respeita a unidade dos coeficientes e procura mudanças de sinal numa malha de idades, refinadas por bisseção. Valores não finitos quebram o trecho; a busca não salta automaticamente por uma região inválida da função.
### Exemplo
Para V(t) = 100t/(1+t), IMA = 100/(1+t) e ICA = 100/(1+t)². Para t > 0, ICA é menor que IMA, portanto não há cruzamento positivo. Isso é uma propriedade matemática da curva, não uma falha do programa. O ICA anual `V(t)−V(t−1)` representa outro cálculo e não deve ser confundido com a derivada.

## Testes estatísticos
### Convenções e decisão
Nos testes bilaterais de médias, diferenças positivas e negativas são consideradas. A decisão do programa usa p < α para rejeitar H0; valores exatamente iguais a α não entram nessa condição. Resultado degenerado, variância nula ou falta de graus de liberdade pode gerar teste indefinido.
### t de Welch: médias independentes
> EP = √(s_A²/n_A + s_B²/n_B); t = (x̄_A − x̄_B)/EP
> ν = (s_A²/n_A + s_B²/n_B)² / [(s_A²/n_A)²/(n_A−1) + (s_B²/n_B)²/(n_B−1)]
ν é a aproximação de Welch–Satterthwaite e pode ser fracionário. O p-valor vem das duas caudas da distribuição t com ν graus de liberdade. O intervalo da diferença é `x̄_A − x̄_B ± t_(1−α/2;ν) × EP`.
Exemplo: médias 100 e 110, variâncias amostrais 100 em ambos os grupos e 10 observações por grupo dão EP = √20 ≈ 4,472, t ≈ −2,236 e ν = 18. O p-valor bilateral é aproximadamente 0,038; a 5%, rejeita-se igualdade das médias sob os pressupostos. Isso não mede a relevância econômica de uma diferença de 10.
### t pareado: diferenças da mesma unidade
> dᵢ = Aᵢ − Bᵢ; t = d̄/(s_d/√n_pares); gl = n_pares − 1
Para diferenças {1,2,3,4,5}, d̄ = 3, s_d² = 2,5 e t ≈ 4,243, com 4 graus de liberdade. É a variância das diferenças que entra na conta, não a soma automática das variâncias de A e B. Pares incompletos são retirados juntos; pares devem ser independentes entre si.
### Teste F de variâncias
> F = s_A²/s_B²; gl₁ = n_A−1; gl₂ = n_B−1
A implementação conserva A/B na razão e calcula o p bilateral pelas duas caudas, limitado a 1. Requer populações normais e independência; é sensível à falta de normalidade. Uma razão 4 significa que a variância de A é quatro vezes a de B; o desvio-padrão é duas vezes maior, não quatro.
### ANOVA de um fator
> SQ_entre = Σ nⱼ(x̄ⱼ − x̄_geral)²; SQ_dentro = ΣΣ(xᵢⱼ − x̄ⱼ)²
> F = [SQ_entre/(g−1)] / [SQ_dentro/(N−g)]
g é a quantidade de grupos e N a quantidade de observações. A média geral é ponderada pelos tamanhos dos grupos. Rejeitar H0 indica que pelo menos uma média difere; esta análise não identifica automaticamente quais pares diferem e não é uma ANOVA de medidas repetidas.
### Brown–Forsythe
Para cada grupo, obtenha a mediana mⱼ, substitua cada observação pelo desvio absoluto `zᵢⱼ = |xᵢⱼ − mⱼ|` e aplique a ANOVA a esses desvios. A hipótese é homogeneidade de variâncias. O método é menos sensível à falta de normalidade que o F clássico, mas ainda pressupõe grupos independentes.
### Identidade de equações de regressão
Com g grupos, k coeficientes por equação e N observações, compare a SQ do ajuste comum reduzido com a soma das SQ dos ajustes separados completos.
> F = [(SQ_reduzido − SQ_completo)/(k(g−1))] / [SQ_completo/(N−gk)]
Exemplo: g = 2, k = 2, N = 40, SQ_reduzido = 500 e SQ_completo = 300 dão `F = (200/2)/(300/36) = 12`, com graus de liberdade 2 e 36. Para modelos lineares aninhados, é a forma clássica do teste; nas famílias não lineares gerais admitidas, a inferência é aproximada. Convergência, identificabilidade e resíduo válido são conferidos antes da conclusão.
### Pressupostos e multiplicidade
Normalidade se refere à distribuição ou aos resíduos pertinentes ao teste, não à exigência de que toda coluna do projeto seja normal. Testar muitos grupos, variáveis ou modelos e escolher somente os menores p-valores aumenta falsos positivos. A tela não aplica automaticamente uma correção geral para todas as análises que o usuário realizou.

## Validação de modelos
### Fórmulas exatamente usadas nas métricas
Nesta ferramenta, o erro tem sinal estimado − observado. Cada modelo usa seus próprios pares numéricos válidos; n pode mudar entre modelos. Métricas relativas têm outro denominador: n_rel é o número de pares cujo observado é diferente de zero e cujo erro relativo é finito.
> eᵢ = ŷᵢ − yᵢ; Bias = Σeᵢ/n; MAE = Σ|eᵢ|/n
> RMSE = √(Σeᵢ²/n); R² = 1 − Σeᵢ²/Σ(yᵢ − ȳ)²
> Erro_médio(%) = Σ(100eᵢ/yᵢ)/n_rel; MAPE(%) = Σ(100|eᵢ/yᵢ|)/n_rel
R² é indefinido quando os observados não têm variação. Erro médio percentual não é `100 × Bias/ȳ`: primeiro se calcula o percentual de cada par, depois a média. RMSE de validação usa n e não n − k.
### Exemplo completo
Observados {100,200,300} e estimados {110,190,330} dão erros {10,−10,30}. A SQ dos erros é 1100 e a SQ total dos observados é 20000.
> Bias = 10; MAE ≈ 16,67; RMSE ≈ 19,15; R² = 0,945
> Erro_médio(%) = 5%; MAPE ≈ 8,33%
O bias positivo indica superestimação média na unidade da resposta, embora uma observação tenha sido subestimada. Bias próximo de zero pode esconder grandes erros de sinais opostos.
### Histograma e comparação
O histograma admite erros relativos entre −1 e 1 e normaliza as frequências pela quantidade dentro dessa faixa. Ele pode omitir erros maiores; as métricas continuam considerando seus pares válidos. O gráfico de dispersão pode usar uma amostra para desempenho visual, enquanto as métricas percorrem os pares válidos da tabela.

## Isolation Forest
### Isolamento por divisões aleatórias
Uma árvore sorteia uma variável com variação e um ponto de corte dentro do intervalo observado. O caminho h(x) conta as divisões para chegar à folha. Folhas com mais de uma observação acrescentam uma correção de comprimento esperado.
> c(n) = 2H_(n−1) − 2(n−1)/n; H_m = 1 + 1/2 + ... + 1/m
> escore(x) = 2^[−média_do_caminho_corrigido(x)/c(ψ)]
ψ é o número efetivo de observações por árvore. c(0) e c(1) são zero. A implementação guarda as correções harmônicas para os tamanhos usuais; quando necessário, usa a aproximação por logaritmo para o número harmônico.
### Significado do escore
Se o caminho médio corrigido for c(ψ), o escore é 0,5. Se for metade de c(ψ), o escore é aproximadamente 0,707: o isolamento foi mais rápido e a observação recebe escore maior. Isso é um indicador de isolamento, não uma probabilidade de dado errado.
### Limiar e empates
Sem limiar manual, os escores do conjunto de treinamento são ordenados e usa-se a posição `teto((1−contaminação) × n) − 1`, limitada aos índices válidos. A classificação de anomalia exige escore estritamente maior que o limiar. Assim, contaminação de 5% não garante exatamente 5% de linhas anômalas, especialmente com empates.

## K-means
### Padronização e distância
Nas linhas elegíveis, calcula-se a média e o desvio com divisor N por variável. Essa escala de padronização não é a variância amostral com divisor N−1 usada em testes de médias.
> zᵢⱼ = (xᵢⱼ − μⱼ)/σⱼ; σⱼ² = Σ(xᵢⱼ − μⱼ)²/N
> d²(zᵢ,cₖ) = Σⱼ(zᵢⱼ − cₖⱼ)²
Sem padronização, uma variável em milímetros poderia dominar outra em metros por sua amplitude numérica. Variáveis constantes são recusadas. A escala é obtida das linhas elegíveis e o ajuste pode usar uma amostra dessas linhas conforme o limite configurado.
### Objetivo e atualização
> Inertia = Σᵢ ‖zᵢ − c_grupo(i)‖²
O algoritmo alterna atribuição ao centro mais próximo e atualização dos centros pela média das observações do grupo. No k-means++, o primeiro centro é aleatório e os seguintes são favorecidos pela distância quadrática ao centro já mais próximo. São feitas inicializações repetidas e conserva-se uma solução válida de menor inertia.
### Silhouette
aᵢ é a distância média do ponto aos demais membros de seu grupo; bᵢ é a menor distância média aos pontos de outro grupo.
> sᵢ = (bᵢ − aᵢ)/máximo(aᵢ,bᵢ); silhouette = média(sᵢ)
Para aᵢ = 1 e bᵢ = 4, sᵢ = 0,75. Valores próximos de 1 indicam boa separação; próximos de zero indicam sobreposição; negativos sugerem que outro grupo está mais próximo. Um grupo com um único ponto recebe contribuição zero na rotina. A silhouette mostrada pode ser calculada sobre uma amostra estratificada, não sobre todas as distâncias da base.

## Volumetria
### Equação de Schumacher–Hall e escala
Na formulação legada, o volume é exponencial no intercepto e potencial nas entradas positivas. A rotina interna recebe DAP e altura nas escalas internas e divide ambos por 10 antes do logaritmo; com entradas já conceituais em cm e m, a forma é a seguinte.
> v = exp(b₀ + b₁ ln(d_cm) + b₂ ln(h_m)) = exp(b₀) d_cm^b₁ h_m^b₂
Isso usa logaritmo natural. Substituir exp(b₀) por 10^b₀ sem mudar a formulação não preserva o modelo. Equações cadastradas com outra fórmula obedecem à definição e aos fatores registrados; não recebem a mesma expressão apenas por serem volumétricas.
### Sensibilidade e exemplo
Mantida a altura, dobrar o diâmetro multiplica o volume por `2^b₁`. Se b₁ = 2, o multiplicador é 4. Para uma equação didática `v = 0,00005 × d² × h`, d = 20 cm e h = 15 m dão 0,30 m³. Os coeficientes são apenas de demonstração, não uma recomendação de modelo florestal.
### Regressão em log e viés
Estimar uma regressão em ln(v) e depois exponenciar não garante a média de v na escala original: em geral, `E[exp(Z)] ≠ exp(E[Z])`. Não presuma que existe um fator de correção lognormal automático. Confira a forma de ajuste, as regras do modelo e a validação na unidade final antes de adotar qualquer correção externa.

## Crescimento e produção
### Forma da curva e coerência
Uma curva fornece `V(t) = f(t; β)` na unidade de idade definida. Uma forma assintótica didática, quando cadastrada com essa expressão, é `V(t) = A[1−exp(−kt)]^m`. A é a assíntota, k regula a velocidade e m a forma; isso não impõe esses significados a coeficientes de outras famílias.
### Avaliação em idades comuns
Se a equação foi ajustada em meses, uma análise na idade de 6 anos precisa avaliar a entrada correspondente a 72 meses, além do fator registrado. Usar t = 6 diretamente na função em meses avalia outra idade.
### Projeção versus predição absoluta
Uma predição absoluta usa f(t). A projeção proporcional usa um valor observado e a razão entre avaliações da curva. A segunda mantém a escala do valor inicial, mas não corrige uma forma de curva inadequada. Curva não monotônica ou com valores negativos pode produzir decréscimos mesmo sem uma operação de manejo modelada.

## Índice de sítio
### Projeção da variável dominante à idade-base
O cálculo atual usa uma equação-guia vigente e a variável observada correspondente, Hd ou DAPd. A unidade de idade da curva é aplicada antes da avaliação.
> IS = Y_dominante_observado × f(idade_base)/f(idade_atual)
Exemplo: Hd observado de 25 m, f(idade_atual) = 20 m e f(idade_base) = 24 m produzem IS = 30 m. O índice mantém a unidade de Y; um índice baseado em DAPd está em unidade de diâmetro, não em metros de altura.
### O que a razão pressupõe
A transformação escala o observado pela forma da curva-guia e exige valores positivos e finitos na idade atual e na idade-base. Não representa automaticamente toda formulação possível de sítio, como modelos polimórficos ou transições de estado com outra equação. O cadastro precisa ser compatível com a operação de razão que a ferramenta realiza.

## Regressões salvas e famílias de modelos
### Coeficientes, preditores e identificabilidade
Uma família define f(x;β). Uma regressão salva contém β̂ ajustado com determinada amostra. Duas expressões escritas de modo diferente podem representar a mesma função; por exemplo, `b₀ + b₁x + b₂x` só identifica a soma b₁+b₂, não os dois coeficientes individualmente.
### Comparações válidas
Aumentar parâmetros reduz a restrição do ajuste, mas consome graus de liberdade. No teste de identidade, k precisa refletir coeficientes efetivamente identificáveis. No uso de AIC/BIC e erro, compare a mesma resposta, a mesma unidade e a mesma base elegível; diferenças de amostra podem mudar a conclusão mais que a fórmula.

## GIS: mapa e camadas
### Coordenadas, áreas e mapa de exibição
A área de um polígono plano pode ser obtida pela soma orientada de seus vértices. Anéis internos representam exclusões. As operações geométricas do planejamento são feitas na coordenada projetada da origem; o mapa converte para o sistema de exibição.
> Área_do_anel = (1/2) × |Σ(xᵢyᵢ₊₁ − xᵢ₊₁yᵢ)|
Com coordenadas em metros, a área fica em m², e 10000 m² = 1 ha. Com coordenadas em graus, aplicar a mesma expressão não produz hectares. Mesmo uma projeção métrica pode ter distorção regional: use um sistema adequado à localização da área de trabalho.
### Distância
No planejamento plano, a separação entre candidatos usa `√[(x₂−x₁)² + (y₂−y₁)²]`. Essa é uma distância no sistema projetado, não o comprimento de uma trilha ou o custo de acesso por terreno e estradas.

## Amostragem
### Intensidade, malha e quantidade
> Espaçamento_da_malha_quadrada = √(ha_por_parcela × 10000)
Com 3 ha/parcela, o espaçamento é aproximadamente 173,21 m. A quantidade necessária pelas regras é uma meta por área, mas o método sistemático atual conserva todos os pontos D1 válidos dentro dos polígonos; por isso, a contagem final pode ultrapassar a meta.
O recuo de borda altera o domínio em que os pontos podem cair. Se o recuo negativo elimina o polígono, o fallback mantém o original: o resultado deve ser revisto quando o protocolo proíbe essa flexibilização.
### Distância máxima (maximin)
> Próximo_candidato = argmax_c min_distância(c, pontos_já_escolhidos)
Escolhe-se o candidato cuja menor distância aos pontos existentes é a maior. É uma regra gulosa: a melhor escolha naquele passo não é uma prova de ótimo global de cobertura. A semente controla o início aleatório e as restrições podem impedir atingir a quantidade desejada.
### Desenho e estimação
Espalhar pontos ajuda a cobrir o território, mas não transforma automaticamente um procedimento em uma amostra probabilística com pesos conhecidos. Interdição de áreas, relocação em campo e retirada de pontos precisam ser documentadas. A sequência numérica da parcela é operacional e não representa uma ordem estatística de importância.

## GRTS: desenho e parâmetros
### Do polígono aos candidatos ponderados
Cada polígono elegível tem área Aⱼ e Nⱼ candidatos válidos. Na aproximação do programa, cada candidato daquele polígono representa `aᵢ = Aⱼ/Nⱼ`. Essa quantidade evita dar mais importância a um polígono somente porque a discretização gerou mais candidatos por hectare nele.
> Massa_igual_por_área: mᵢ = aᵢ
> Massa_proporcional: mᵢ = aᵢ × auxiliarᵢ
O campo auxiliar é copiado do polígono para seus candidatos e deve ser positivo. Se ele representa um total por polígono, e não uma intensidade por unidade de área, usá-lo sem adequação pode ponderar a área duas vezes em relação ao objetivo pretendido.
### Normalização das probabilidades
As probabilidades somam a quantidade desejada n e nenhuma ultrapassa 1. Sem unidades de certeza, são proporcionais às massas. Quando uma probabilidade calculada excede 1, a unidade recebe 1 e a quantidade restante é redistribuída entre as demais.
> πᵢ = n × mᵢ/Σmⱼ, enquanto todos os valores forem ≤ 1
> Σπᵢ = n; peso_do_quadro wᵢ = 1/πᵢ; peso_em_hectares = aᵢ/πᵢ
Na seleção igual por área, sem certezas, aᵢ/πᵢ = A_estrato/n. Para 12 parcelas num estrato de 36 ha, cada parcela representa nominalmente 3 ha. No quadro finito uniforme de 120 candidatos, π = 0,1 e peso_do_quadro = 10; dez candidatos representados não significam dez hectares.
### Categorias e exemplos
Por categoria, as quantidades esperadas definem a soma das probabilidades da categoria dentro do estrato. Com duas categorias de 10 ha e expectativas de 2 e 8 parcelas, os pesos em área, na situação sem certezas, são 5 ha e 1,25 ha respectivamente. A mesma média simples das dez medições não representa automaticamente a média territorial.
### Endereço e sorteio
O território do estrato é enquadrado num quadrado, dividido em quatro quadrantes e subdividido recursivamente. Rótulos aleatórios formam endereços base 4. Ordenam-se os candidatos pelos endereços e cada candidato ocupa um segmento de comprimento πᵢ numa linha de comprimento n.
> Sorteio_da_linha: u, u+1, ..., u+n−1, com 0 ≤ u < 1
Os segmentos atingidos fornecem n unidades distintas quando as probabilidades são válidas. Não é apenas escolher o ponto mais distante. A aleatorização hierárquica e o início na linha fazem parte da reprodução da seleção.
### Confiança, erro e CV no planejamento
> n_planejado = teto[(z_(1−α/2) × CV% / erro_relativo%)²]
Para confiança de 95%, z ≈ 1,96, CV = 30% e erro desejado de 10%, o cálculo dá teto(34,573...) = 35 parcelas, respeitando também o mínimo operacional de duas. É uma aproximação normal para planejamento baseada no CV informado. Não é a variância espacial GRTS e não usa o número artificial de candidatos como tamanho finito da população.
### Reservas, distância e pesos nominais
Com r reservas, o sorteio inicial usa n+r candidatos e a ordem dos endereços é invertida hierarquicamente para definir Base e Reserva. A rotina pode repetir o desenho para buscar a distância desejada no conjunto do estrato e conserva a melhor distância mínima obtida.
Condicionar a escolha à distância e separar reservas altera o procedimento em relação à linha probabilística básica. Por isso PI_EXATA fica falso nesses casos e os pesos exportados são nominais do desenho de base. A implementação não recalcula probabilidades finais condicionais nem oferece um estimador de variância para esse procedimento adaptado.
### O que seria necessário para estimar volume depois da coleta
Com pesos de área válidos, um estimador de total pode ter a forma `T̂ = Σ peso_ha,i × yᵢ`, se yᵢ é uma taxa em m³/ha; uma média territorial pode usar `T̂/área_elegível` ou a razão apropriada dos totais. Variância e IC exigem desenho, não resposta, substituições e dependências coerentes. Essas contas são uma explicação de uso estatístico dos pesos, não uma análise pós-coleta já executada pela ferramenta.

## Configurações: inventário, colunas e unidades
### Análise dimensional antes de processar
> d_cm = d_mm/10; h_m = h_dm/10; 1 ha = 10000 m²
As conversões precisam ocorrer uma vez em cada passagem prevista pelo fluxo. O cálculo interno pode trabalhar numa escala padronizada e aplicar fatores de entrada no modelo; isso não autoriza converter manualmente a planilha e manter uma configuração que ainda presume a escala antiga.
### Propagação de escala
Como g depende de d², multiplicar d por 10 multiplica g por 100. Numa equação potencial, um erro de fator c no diâmetro multiplica o volume por c^b₁. Um erro em área afeta inversamente os indicadores por hectare: se a área for informada como metade da real, a expansão por hectare dobra.
Confira a convenção atual de dimensões da parcela: comprimento e largura entram divididos por 10 na rotina de área. As declinações são ângulos em graus, convertidos a radianos na função cosseno; 30 graus não deve ser registrado como 0,30 esperando representar a mesma inclinação.

## Configurações: cadastro florestal
### Idade cronológica
Na rotina de preparo, as datas são convertidas para uma contagem de dias e a idade em anos segue a convenção abaixo.
> idade_anos = piso(data_medição − data_plantio)/365,25
Uma diferença de 2191 dias corresponde a aproximadamente 5,999 anos. A idade não é somente ano_da_medição − ano_do_plantio; dias e meses importam, particularmente perto da mudança de idade.
### Área e vínculos
A área efetiva é um atributo do talhão cadastrado. Repeti-la em 100 linhas de árvores não significa 100 vezes a área territorial. As consolidações precisam deduplicar o atributo pela unidade correta; uma chave equivocada pode ligar árvores a uma data ou área de outro talhão.
Idade negativa ou data inválida deve ser investigada. O fallback de idade, quando usado, precisa ser interpretado pela regra configurada; uma idade inferida de código não adquire a precisão de uma data de plantio conhecida.

## Configurações: identificação
### Chave composta e independência
Uma chave composta representa uma combinação de valores. Se a unidade observacional é árvore dentro de parcela, chaves só com o número da árvore repetido não identificam toda a base. A duplicidade se refere à igualdade da chave e das condições relevantes, não à igualdade de uma medida.
### Por que muda as estatísticas
Duplicar todos os registros sem reconhecer a dependência pode manter a média e aumentar artificialmente n. Aplicar EP = s/√n como se fossem novas unidades independentes passa a sugerir maior precisão sem nova coleta. A data também precisa distinguir uma ocasião quando a finalidade é consolidar, mas ser tratada como ocasião a comparar quando a finalidade é parear.
As configurações definem a identidade operacional. Elas não estimam automaticamente um coeficiente de correlação intraparcela nem corrigem o erro-padrão por conglomerados.

## Configurações: altura e modelos
### Correção de viés
Com alturas observadas hᵢ e previsões gerais pᵢ para o conjunto de calibração, a correção aditiva de viés usa a diferença entre médias.
> h_corrigida = p + (média(h) − média(p))
Se as previsões médias são 18 m e as observações 20 m, a correção aditiva é +2 m. Isso não multiplica todas as alturas por 20/18; multiplicação seria outra regra.
### Correção linear e Ridge
Com dados suficientes e variação nas previsões, a correção linear ajusta intercepto e inclinação. Com suporte para a etapa de última camada, o ajuste Ridge trabalha com as saídas ocultas centradas Zc e resposta centrada yc.
> h_corrigida = a + bp; b = Σ(pᵢ−p̄)(hᵢ−h̄)/Σ(pᵢ−p̄)²; a = h̄ − bp̄
> Ridge: (ZcᵀZc + αI)β = Zcᵀyc; intercepto = h̄ − média(Z)ᵀβ
α penaliza os pesos dessa etapa; o intercepto é reconstruído a partir das médias. Mínimos de amostras, variância e condições numéricas controlam as etapas que podem ser usadas. Uma calibração apoiada em poucas árvores não garante melhora fora do seu grupo.
### Estatísticas auxiliares
Média, soma, mediana, desvio-padrão e outras agregações são calculadas nos grupos e filtros configurados para as entradas do modelo. O desvio-padrão amostral usa n−1; uma variável constante ou filtro que elimina todas as linhas pode tornar a entrada inválida. Guarde essas definições junto ao pacote e ao resultado.

## Configurações: estatística do inventário
### Contas do relatório por grupo
No relatório de inventário, a média por grupo é formada com as taxas das parcelas. A variância usa os volumes finitos com divisor de amostra; n no erro-padrão é a contagem de parcelas do grupo. Confira completude e validade dos volumes antes de interpretar essa conta.
> s² = Σ(V_ha,i − V̄_ha)²/(n_válidos−1); CV% = 100s/V̄_ha
> EP = s/√n_parcelas; erro_absoluto = t_(1−α/2;n_parcelas−1) × EP
> erro_relativo% = 100 × erro_absoluto/V̄_ha; IC = V̄_ha ± erro_absoluto
Os indicadores de dispersão e erro dependem do mínimo de parcelas configurado e dos valores disponíveis. A rotina protege situações de média não positiva conforme a definição do relatório; um zero exibido sob essas condições não deve ser confundido com precisão perfeita.
### Exemplo com três parcelas
Para taxas {180,200,220} m³/ha, média = 200, s = 20 e EP ≈ 11,547. Com confiança de 95% e 2 graus de liberdade, t ≈ 4,303. O erro absoluto é aproximadamente 49,68 m³/ha, o erro relativo 24,84% e o IC [150,32; 249,68]. O quantil t é grande porque há poucas parcelas, apesar da média aparentemente estável.
### Consolidação do relatório
Os totais de volume usam médias e áreas efetivas dos talhões. Na linha TOTAL do relatório atual, o campo de erro relativo é consolidado por ponderação de área dos erros dos grupos que contribuem; ele não resulta de recalcular uma variância conjunta de toda a amostra. Não o interprete como novo IC t combinado nem como variância estratificada GRTS.
### Gini e desigualdade
Quando calculado sobre valores não negativos ordenados x₍ᵢ₎, o índice implementado segue a expressão abaixo, sem multiplicar automaticamente por n/(n−1).
> Gini = 2Σ[i × x₍ᵢ₎]/[nΣx₍ᵢ₎] − (n+1)/n
Para {0,10,20}, o resultado é aproximadamente 0,444. Todos os valores zero produzem 0 nessa rotina. Gini mede desigualdade do indicador que foi usado, não erro amostral ou qualidade do ajuste.

## Configurações: regras de volumetria e comparação IFC
### Relação entre observado e esperado
Quando o relatório usa o indicador de comparação entre volume observado e esperado, a razão é calculada somente com referência positiva e válida.
> ICV = V_observado/V_esperado
Se observado = 180 e esperado = 200 m³/ha, ICV = 0,90. Isso significa 90% da referência, não um p-valor de 0,90 nem um fator que já tenha sido aplicado ao volume. Os limiares de cor classificam essa razão conforme a configuração; não recalculam as previsões.
### Sensibilidade à referência
Uma equação ou curva esperada inadequada pode produzir um alerta com medições corretas. Antes de interpretar a razão, confira ID do modelo, unidade, idade, área e domínio de aplicação. Trocar os limiares altera a sinalização, não a dispersão observada nem a incerteza do inventário.

## Configurações: colunas do resumo direto
### Cálculo versus apresentação
Desmarcar uma coluna opcional não elimina sua contribuição interna para outros cálculos. A taxa por hectare, por exemplo, ainda depende de volume e área mesmo que uma coluna auxiliar deixe de ser gravada no nível por árvore.
### Conferência entre níveis
Uma soma de taxas por hectare geralmente não tem a interpretação de volume total. Use a unidade e a regra de expansão do nível de saída. Linhas de parcela representam amostras; campos de expansão territorial pertencem aos níveis onde a área efetiva é aplicável, conforme as regras do Processador.
