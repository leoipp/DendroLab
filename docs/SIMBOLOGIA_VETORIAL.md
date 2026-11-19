# Simbologia vetorial

Em Conteúdos, clique com o botão direito em uma camada SHP e escolha **Simbologia…**.

## Símbolo único

Na aba Símbolo, ligue ou desligue preenchimento e contorno separadamente. Desligar um componente mantém sua cor e suas medidas para quando for ligado novamente. O preenchimento serve para polígonos e pontos; o contorno também serve para linhas. Hachuras são aplicadas aos polígonos.

Clique na amostra de cor para abrir o seletor: escolha matiz e saturação na roda, ajuste luminosidade e transparência ou informe a cor hexadecimal. **0% de transparência é opaco; 100% é invisível.** O fundo quadriculado permite conferir a transparência. As setas do teclado também alteram a roda; os sliders aceitam navegação por teclado.

Preenchimento e contorno têm transparências independentes. Estilo/espessura de linha, hachura e tamanho dos pontos permanecem disponíveis. As medidas usam unidades de tela para não variar com o zoom.

## Valores únicos

Escolha **Valores únicos**, selecione um campo do DBF e clique em **Classificar valores**. O editor lê os valores salvos no arquivo, sem modificar os atributos.

- Cada valor distinto recebe uma cor, editável pela mesma roda de cores com transparência.
- O checkbox da categoria controla a visibilidade de suas feições.
- Valores vazios são agrupados em uma categoria própria. Maiúsculas e minúsculas são diferenciadas.
- Registros marcados como excluídos no DBF não entram nas categorias.
- A opção Outros valores controla feições com valores novos ou sem categoria. Elas usam o símbolo padrão da aba Símbolo.
- A paleta é usada para novas categorias. Reclassificar o mesmo campo preserva cores e visibilidade dos valores já existentes. **Recolorir categorias** aplica a paleta atual a todas, preservando suas transparências.
- As cores das categorias controlam preenchimento de polígonos/pontos e cor de linhas. O contorno dos polígonos mantém a configuração comum da aba Símbolo.
- A classificação categorizada tem prioridade sobre as cores por rodada de amostragem. O símbolo único pode continuar preservando as cores das rodadas.

O editor aceita até **500 categorias** por camada. Ao trocar o campo, é necessário classificar novamente antes de aplicar. Edições na tabela de atributos precisam ser salvas no DBF antes da classificação.

## Aplicação e armazenamento

Aplicar atualiza a camada já aberta no mapa. Cancelar descarta as mudanças do editor; cancelar o seletor de cores mantém a cor anterior. Salve o projeto DLB para persistir o estilo. O SHP e o DBF originais não são alterados. Estilos anteriores de símbolo único continuam compatíveis.

O vínculo de categoria usa o índice físico do registro no shapefile, preservando a correspondência com o DBF mesmo quando existem geometrias nulas. Se o campo ou o DBF deixar de existir, o GIS informa o problema e usa a configuração de outros valores.

## Referências de projeto

A organização em símbolo único/categorizado, as cores editáveis por categoria e o tratamento de outros valores foram estudados na [documentação de simbologia do QGIS 3.44](https://docs.qgis.org/3.44/en/docs/user_manual/working_with_vector/vector_properties.html). A roda HSV, entrada hexadecimal e ajuste de transparência no mesmo seletor seguem os princípios descritos no [seletor de cores do QGIS](https://docs.qgis.org/3.44/en/docs/user_manual/introduction/general_tools.html#color-selector). A implementação é nativa WPF e não depende da instalação do QGIS.
