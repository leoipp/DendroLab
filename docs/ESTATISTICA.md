# Diagnósticos de crescimento e testes estatísticos

As ferramentas executam em C#/.NET. As distribuições t e F são calculadas pelo
Math.NET Numerics gerenciado; nenhum runtime Python/R ou provedor nativo é usado.

## ICA e IMA

Em **Ferramentas > ICA e IMA**, selecione até 12 curvas: ajustes convergidos de
gráficos ou versões em Crescimento e produção. Para ajustes de gráficos, confirme
que X é idade e Y é produção acumulada e informe se X está em anos ou meses.
Versões cadastradas devem ter uma única entrada de idade com unidade conhecida.

O intervalo é informado em anos. A unidade dos coeficientes originais é preservada
na avaliação da equação, inclusive quando usa meses e fator de conversão.

- IMA(t) = V(t)/t, com t em anos.
- ICA instantâneo: derivada numérica de cinco pontos em relação à idade em anos.
- ICA anual: V(t) − V(t−1 ano), disponível quando t ≥ 1 e ambos os valores são finitos.

O gráfico usa linha contínua para IMA e tracejada para ICA, com marcas nos
cruzamentos. O resultado registra a faixa analisada, cruzamentos, máximos/mínimos
locais do IMA (na opção de derivada) e maior IMA encontrado no intervalo.
Curvas coincidentes não recebem uma idade única de culminação. Ausência de
cruzamento se refere somente ao intervalo informado; não invalida a curva.
A busca usa amostragem e bisseção; não é uma demonstração analítica de inexistência
de raízes, especialmente para fórmulas com oscilações estreitas.
O cruzamento do ICA por diferença anual não é necessariamente o máximo exato do
IMA contínuo. Valores não finitos e incrementos negativos são informados.

A distinção entre incremento anual e incremento médio segue o
[Code of Forest Mensuration Practice](https://fennerschool-associated.anu.edu.au/mensuration/rwg2/code/3-8.htm).

## Identidade de modelos

Em **Ferramentas > Identidade de modelos**, escolha tabela Excel ou banco DENDRO,
X, Y, coluna de grupos e uma família 1P do cadastro do projeto. Clique em
**Listar grupos**, marque de 2 a 25 grupos e informe α. Não é um teste de duas
colunas de previsões contra intercepto 0 e inclinação 1.

H0: uma única equação, com os mesmos coeficientes, representa todos os grupos.
O sistema ajusta a mesma família em cada grupo (modelo completo) e no conjunto
dos grupos selecionados (modelo reduzido). Com g grupos, k coeficientes e N
observações válidas:

`F = ((SQreduzido − SQcompleto)/(k·(g−1))) / (SQcompleto/(N−g·k))`

Os ajustes precisam convergir, ter variação em X e graus de liberdade residuais.
Se a soma dos resíduos separados exceder a do ajuste comum além da tolerância
numérica, o sistema recusa a inferência e pede revisão da convergência.
Resíduo nulo ou estatística degenerada gera resultado indefinido.
Também é conferido o número de coeficientes usados e a identificabilidade
numérica da matriz de derivadas dos ajustes; parâmetros redundantes impedem o teste.

Para polinômios reconhecidos, trata-se do teste F de modelos lineares aninhados.
Nas famílias gerais, a aplicação da estatística F é explicitamente apresentada
como aproximação, sem alegação de teste exato para qualquer equação.
Supõe erros independentes, aproximadamente normais e variâncias homogêneas.
Medições repetidas da mesma parcela não podem ser tratadas automaticamente como
réplicas independentes. A ferramenta atual não ajusta estruturas de correlação,
modelos mistos, blocos ou pesos heterocedásticos.

Referências: [Regazzi — identidade de modelos](https://apct.sede.embrapa.br/pab/pt_BR/article/view/4446),
[Regazzi — modelos não lineares](https://locus.ufv.br/items/a82a0680-50bb-43ea-8c03-7315f67c67f2),
[Regazzi e Silva — aproximações e delineamento](https://new.scielo.br/j/rceres/a/LwMkT378sDKLJDMTjQWYsRG/?ilang=pt_BR&lang=pt).

## Outros testes

**Testes estatísticos** oferece:

| Teste | Organização | Hipótese nula |
| --- | --- | --- |
| t de Welch bilateral | Dois grupos ou duas colunas independentes | Médias iguais |
| t pareado bilateral | Duas colunas; cada linha é um par | Diferença média A − B igual a zero |
| F de variâncias bilateral | Dois grupos ou duas colunas independentes | Variâncias iguais |
| ANOVA de um fator | Dois ou mais grupos | Todas as médias iguais |
| Brown–Forsythe | Dois ou mais grupos | Variâncias iguais |

Welch não pressupõe variâncias iguais e usa graus de liberdade de
Welch–Satterthwaite. Os testes t incluem IC bilateral para a diferença média.
O F de variâncias pressupõe populações normais. Brown–Forsythe aplica ANOVA aos
desvios absolutos em relação à mediana de cada grupo. ANOVA pressupõe variâncias
homogêneas e não identifica, isoladamente, quais pares diferem.

No teste pareado, pares incompletos são omitidos juntos. Nas duas colunas
independentes, cada amostra recebe sua própria contagem de valores válidos.
Os números de observações omitidas são apresentados. Não rejeitar H0 não prova
igualdade ou equivalência. Comparações múltiplas/post-hoc não são executadas
automaticamente e não há verificação automática de todos os pressupostos.

Referências: [NIST — teste t](https://www.itl.nist.gov/div898/handbook/eda/section3/eda353.htm),
[NIST — teste F](https://www.itl.nist.gov/div898/handbook/eda/section3/eda359.htm),
[NIST — Levene](https://www.itl.nist.gov/div898/handbook/eda/section3/eda35a.htm).

## Resultados e persistência

As análises abrem em abas centrais com resultado, gráfico quando aplicável e
dados/ajustes. É possível exportar XLSX e, nos resultados com gráfico, SVG.
Parâmetros, identificação da fonte, equação e resumo dos resultados ficam em
`_dendro.analyses` no projeto. A análise aparece abaixo da tabela de origem ou
em **Análises**, quando não está vinculada a uma única fonte.

Reabrir apresenta o resumo salvo; **Reexecutar análise** recalcula com a fonte
atual e atualiza esse resultado. A fonte de dados não é congelada pela análise.
Para preservar uma entrega anual, exporte o relatório e mantenha a base utilizada.
Médias, t e F de variâncias usam leitura sequencial e momentos acumulados;
identidade mantém os pares X/Y e Brown–Forsythe mantém os valores necessários
às medianas em memória.

## Validação

Os testes confrontam t e ANOVA com probabilidades de forma fechada e o F de
variâncias com os valores críticos do exemplo do NIST. Também cobrem identidade
com equações iguais/diferentes, aproximação não linear, dados pareados incompletos,
leitura de banco, conversão anos/meses, cruzamento de Schumacher, mínimo de IMA,
curvas coincidentes, diferença anual e persistência dos resultados na interface.

## Fronteira de eficiência

O envelope empírico foi separado de Regressões 1P. Ele recebe um gráfico já existente e usa suas colunas X/Y, estrato, filtro de grupo, escalas e pontos excluídos, sem alterar o gráfico original.

- Máximo/mínimo: seleciona um par X/Y observado em cada faixa. Empates são resolvidos de forma determinística pela ordem das linhas.
- Percentil: ordena Y e interpola linearmente na posição `(n − 1) × p`. A fronteira inferior usa `1 − p`; X é a média da faixa.
- Média de cauda: usa `ceil(n × percentual / 100)` pontos mais altos ou mais baixos, com pelo menos um ponto; X e Y são as médias dos pontos selecionados.
- Faixas de largura igual: divide o intervalo observado de X por estrato. Se X for constante, todos os pontos entram na primeira faixa.
- Faixas de quantidade igual: ordena por X e linha, distribui por posição; empates em X podem ficar em faixas adjacentes.

O usuário configura 1 a 1.000 faixas e o mínimo de observações por faixa. Faixas vazias ou abaixo do mínimo são omitidas e contadas. A tabela registra estrato, lado, faixa, intervalo X, N e coordenadas resultantes; máximos/mínimos incluem a linha original. Linhas ligam os pontos disponíveis, inclusive sobre intervalos omitidos. Esses segmentos são somente uma visualização do envelope, sem regressão. Não é DEA, fronteira estocástica ou teste de eficiência causal. Cada execução pode ser exportada como tabela XLSX e gráfico SVG.
