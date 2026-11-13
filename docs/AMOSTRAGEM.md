# Amostragem de talhões

A ferramenta fica em **Ferramentas → Amostragem**, com GRTS disponível no seletor de método. Ela recebe um shapefile de
polígonos, calcula a área de cada talhão e sugere pontos de parcela. A saída é
um shapefile de pontos com os atributos da feição de origem, um CSV e um mapa
SVG com as parcelas coloridas pela rodada.

Ao clicar em **Gerar no mapa**, a amostra é salva como nova camada em **Conteúdos → Dados vetoriais** e desenhada no mapa. Os pontos são coloridos por rodada: D1 azul, D2 laranja, D3 roxo e D4 vermelho. Clique com o botão direito sobre a amostra em Conteúdos e escolha **Exportar amostragem…** para copiar o shapefile, CSV e SVG. O checkbox antes de cada camada liga ou desliga sua exibição no mapa; essa escolha é guardada no projeto.

## Métodos disponíveis

- **Sistemática atual (D1 a D4):** replica a malha fixa baseada nos limites do
  conjunto, com D1 em centros ou vértices; D2 tenta pontos da malha
  complementar fora das zonas de influência; D3 usa pontos internos dos
  maiores fragmentos disponíveis; D4 coloca um ponto representativo em
  talhões ainda sem parcela. Mantém os quatro rótulos para facilitar a
  comparação com o `Sampler.py`.
- **Sistemática com início aleatório:** desloca a malha por uma origem aleatória
  reproduzível pela semente. Usa pontos complementares quando a malha não
  alcança a quantidade definida para um talhão.
- **Distância máxima (maximin):** começa em um candidato
  aleatório e escolhe sucessivamente o candidato mais distante dos pontos já
  escolhidos, tentando respeitar a distância mínima configurada. É uma
  heurística maximin reproduzível pela semente.
- **GRTS:** permite usar cada talhão, a área inteira ou um campo de atributos como estratificação. Aproxima a área útil por uma grade
  hexagonal com densidade configurável de candidatos por parcela solicitada, atribui
  endereços base 4 por subdivisões recursivas randomizadas em quadrantes e
  seleciona uma amostra sistemática ao longo desses endereços. O tamanho da
  amostra é fixo por estrato. O CSV e o shapefile incluem probabilidades, pesos, estrato, tipo de parcela e ordem de substituição.

## Parâmetros específicos do GRTS

- Quantidade: por intensidade, fixa em cada estrato, editada por estrato ou planejada com confiança, erro relativo e CV. O planejamento usa aproximação normal `teto((z × CV / erro)²)` e não garante a precisão alcançada em campo.
- Probabilidades: iguais por unidade de área; proporcionais a um campo numérico positivo por unidade de área; ou definidas pelas quantidades esperadas de cada categoria dentro de cada estrato. As quantidades por categoria precisam ser positivas e somar a quantidade de base do estrato; são expectativas, não cotas garantidas por realização.
- Densidade: quantidade de candidatos por parcela, com limite de um milhão de candidatos por estrato.
- Distância e tentativas: busca a distância desejada entre todas as parcelas de base e reservas do estrato. A melhor tentativa é mantida; se não atingir a distância, o aviso informa o valor obtido e a quantidade de parcelas não é reduzida.
- Substitutas: número de reservas por estrato, ordenadas pela reversão do endereço hierárquico. `TIPO` distingue Base de Reserva; `RES_ORDEM` registra a ordem de uso.

Sem distância e sem reservas, as probabilidades são exatas para o quadro finito daquela execução e aproximam o quadro contínuo. Cada candidato representa uma fração da área elegível de seu polígono; `PESO = 1 / PI_APROX` e `PESO_HA` expressa expansão em hectares. O cálculo proporcional admite unidades de certeza, limitando probabilidades a 1 e redistribuindo o restante.

Com ajuste de distância ou reservas, os valores registrados são nominais do desenho de base: `PI_EXATA=false` sinaliza que não são probabilidades exatas do procedimento condicionado. Reservas não devem ser somadas à base como se todas fossem medições efetivamente realizadas. Parcelas históricas obrigatórias e estimadores de variância ainda não são implementados.

A exportação inclui `.design.json`, com as opções, semente, número de candidatos, tamanhos de base/reserva por estrato, área elegível, distância pedida/obtida, tentativas e avisos. O raio de exclusão dos outros métodos fica desabilitado ao selecionar GRTS; use a distância mínima específica do desenho.

## Regras e campos

A quantidade necessária segue as faixas editáveis da tela. Para talhões na
faixa maior, usa `ceil(area_ha / hectares_por_parcela)`. O recuo da borda é
aplicado antes de alocar os pontos; se esse recuo eliminar o polígono, o
polígono original é mantido para que o talhão continue amostrável. A distância
mínima é usada nos fallbacks e nos métodos alternativos.

A identificação usa o campo configurado, com um identificador `T0001` gerado
quando ele não existe. O sistema copia os atributos de origem, adiciona
sequência, rodada e coordenadas e pode ordenar internamente por X/Y. A
sequência por lote usa os 14 primeiros caracteres do identificador; o campo de
projeto pode separar sequências.

Os seletores de campo identificador e campo de projeto são carregados da tabela de atributos `.dbf` do shapefile selecionado. Trocar o shapefile atualiza as opções. `REF_ID` e `PROJETO` são selecionados automaticamente quando existem; o campo de projeto também pode ficar sem seleção de atributo.

O arquivo precisa ter `.prj` e usar um sistema projetado em metros. A
ferramenta interrompe a execução em coordenadas geográficas ou em pés para
evitar calcular áreas, buffers e distâncias em unidades incorretas. Quando
necessário, reprojete o shapefile para UTM antes de executar.

## Referências metodológicas

- Stevens e Olsen (2004), [Spatially Balanced Sampling of Natural Resources](https://doi.org/10.1198/016214504000000250).
- Dumelle et al. (2023), [spsurvey: Spatial Sampling Design and Analysis](https://www.jstatsoft.org/article/view/v105i03), incluindo amostragem de recursos areais por uma grade hexagonal densa.
