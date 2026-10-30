# Auditoria do processamento IFPC — 23/10/2026

Referências lidas, sem executar nem alterar Python:

- `G:\PycharmProjects\Dendrolab\config.json`: aponta para um backend IFPC externo;
  não contém os parâmetros do inventário.
- `G:\PycharmProjects\IFPC\config.json`: parâmetros do backend disponíveis neste computador.
- `G:\PycharmProjects\IFPC\cli\IFProcessing.py`: carregamento nas linhas 507–618,
  processamento e geração do relatório.

Taylor está **fora do escopo por solicitação do usuário**. Não é pendência desta entrega.
O original usa uma exportação de contrato de incertezas, não um diagrama de Taylor.

## Parâmetros efetivos do processamento

| Caminho no config | Situação no DENDRO C# |
| --- | --- |
| `arquivo_entrada.chave` | Motor já consultava; agora editável em Configurações > Inventário. |
| `arquivo_entrada.colunas.talhao/parcela/lote/dap/altura/data_medicao` | Mapeamentos persistidos no projeto, editáveis e usados pelo motor. |
| `arquivo_entrada.colunas.comprimento_parcela/largura_parcela/declinacao_comprimento/declinacao_largura` | Mesma parametrização; não é obrigatório usar os nomes Compr/Larg/Dec_Compr/Dec_Larg. |
| `cadastro.caminho` | Caminho único em Cadastro florestal, usado pelo processador. Projetos antigos reaproveitam `_dendro.registry` quando necessário; a referência duplicada é retirada ao salvar. |
| `cadastro.worksheet` | Seleção de worksheet/cabeçalho pelo diálogo Excel; opções específicas do arquivo têm precedência. |
| `cadastro.chave` | Editável em Cadastro florestal; pode ser diferente da chave do inventário. |
| `cadastro.colunas.data_plantio/area_efetiva` | Editáveis, utilizadas no cálculo de idade e na consolidação de área. |
| `ajuste_altura.debug` | Checkbox explicitamente identificado como Debug. Controla colunas intermediárias, VMI_real e exportação de CALCULOS. |
| `ajuste_altura.h_mode` | KEEP_FILE, MODEL_GENERAL e FINETUNE_MODEL executam em C#. CALIBRATE continua recusado explicitamente. |
| `ajuste_altura.modelo_geral.caminho` | ZIP selecionado no projeto, carregado diretamente; mantém suporte a conjunto legado. |
| `ajuste_altura.features` | Lista cadastrável; no modelo pré-treinado, a seleção e a ordem efetivas das entradas são as do metadata do modelo. |
| `ajuste_altura.variaveis_customizadas` | Cadastro de fórmula, diferença de datas e coluna do cadastro; parser C#, não execução de Python arbitrário. |
| `ajuste_altura.estatisticas_parcela` | Cadastro Nome/Coluna/Função/Fórmula/Filtro. |
| `ajuste_altura.finetune.min_amostras_bias/min_amostras_linear/min_amostras_finetune/ridge_alpha` | Aplicados à calibração escalonada por viés, linear e Ridge da última camada. |
| `volume.idade_limite_equacao_geral` | Agora editável em Processamento, além de já utilizado no motor. |
| `volume.equacoes` | ID, b0/b1/b2, nome/família, fórmula e até 32 entradas com origem, coluna, unidade e fator. Legados mantêm Schumacher–Hall. |
| `curvas_crescimento.equacoes` | Família, coeficientes, fórmula, ano e vigência manual por ID. Entradas independentes (até 32), inclusive outras variáveis além de idade, com origem, unidade, fator e agregação por parcela. A definição fica preservada por versão. |
| `comparativo_ifc.ativo` | Agora editável; habilita/desabilita os cálculos e as colunas de comparação. |
| `comparativo_ifc.limiares.verde/amarelo` | Agora editáveis; aplicados à formatação condicional do relatório. |
| `comparativo_ifc.descricao.*` | Agora editáveis e registradas em INFORMACOES. |
| `estatisticas.nivel_confianca` | Agora editável; usado no t de Student e nos intervalos. |
| `estatisticas.min_parcelas_estatistica` | Agora editável; condiciona o cálculo das estatísticas de dispersão e erro. |
| `estatisticas.erro_maximo_alerta` | Era ignorado; agora editável e usado no destaque do erro relativo. |
| `estatisticas.chave_temporal_ativa` | Agora editável; separa remedições por ano nos agrupamentos. |
| `saida.sufixo_arquivo` | Era fixo em _DENDRO; agora editável e utilizado ao montar os nomes do lote. |
| `saida.sheets.ajustado/h_ajuste/resumo_byplot/resumo_bytalhao/estatisticas_inv/graficos` | Eram ignoradas; agora checkboxes que controlam as planilhas exportadas. |

As opções adicionais `saida.sheets.modelos_aplicados/informacoes/calculos` permitem
desativar as planilhas auxiliares do DENDRO. CALCULOS também depende de debug ativo.
O nome final continua combinando pasta, prefixo, nome da entrada e sufixo configurado,
com numeração para evitar sobrescrita. As opções específicas de worksheet e cabeçalho
continuam em `_dendro.file_options`.

## Chaves legadas sem efeito no IFProcessing consultado

Não se deve criar um comportamento novo e chamá-lo de paridade apenas porque a chave
existe no JSON. A leitura do código revelou estes casos:

- `cadastro.ativo`: carregado e exibido, mas o fluxo chama o merge do cadastro sem
  consultar essa opção. O processamento C# exige cadastro válido explicitamente.
- `cadastro.colunas.talhao`: o vínculo utiliza `cadastro.chave`.
- `volume.modelo` e `volume.usar_equacoes_por_codigo`: o fluxo consultado aplica
  Schumacher-Hall e a lista de equações; essas chaves não selecionam outra implementação.
- `saida.formato`: o exportador consultado gera XLSX.
- `saida.sheets.dashboard` e `saida.sheets.comparativo_ifc`: não geram planilhas
  próprias no exportador consultado. A comparação IFC está nos resumos.
- O original carrega `saida.sheets`, mas seu exportador completo não consulta esse
  dicionário ao criar as seis planilhas. O DENDRO passou a respeitar os seis controles.
- `ajuste_altura.por_lote` e `fallback_rna_geral` são parâmetros da proposta de
  treinamento CALIBRATE, ainda não portado. O FINETUNE_MODEL original percorre lotes
  e decide o fallback global separadamente.
- `finetune.epochs/learning_rate/patience/batch_size` são carregados, mas a última
  camada do método consultado é ajustada por Ridge; não há treinamento Adam nessa etapa.

## Lacunas funcionais ainda reais, sem Taylor

1. **CALIBRATE:** falta o treinamento de uma RNA nova em C#, com Adam/MAE,
   normalização por fold, validação cruzada, early stopping e seleção do melhor fold.
   Os campos `rede_neural.*` não passam a funcionar apenas por estarem na janela.
2. **H Ajuste:** métricas, método e contagens por lote foram acrescentados nesta
   revisão. Falta comparar o layout e os casos de fallback com uma saída original congelada.
3. **Debug/KEEP_FILE:** foram acrescentados plantio, área efetiva, H_obs/H_est,
   ano da medição, diferenças de datas e coeficientes das equações. Falta comparar
   o conjunto e a ordem das colunas em cada modo com uma saída original congelada.
4. **Gráficos:** faltam histogramas e parte dos painéis e posicionamentos do XLSX
   original. Os gráficos atuais de dispersão não são equivalentes ao painel completo.
5. **Equivalência de resultado:** falta comparar uma base real com relatório original
   congelado, mesmos parâmetros e modelo, verificando linhas, colunas, unidades,
   valores ausentes, árvore, parcela, talhão e diagnóstico da RNA.

O motor mantém diferenças deliberadas de validação: recusa chaves duplicadas,
cadastro ausente, curvas vigentes ausentes e saída já existente, em vez de prosseguir
com um relatório potencialmente incompleto. Não há alegação de paridade integral.

## Outras seções do config

`criterio_manejo`, `pre_corte_manejo`, `cadastro_fomento`, `gee`, `batch`, `sampler`,
`fechamento`, `ndvi_sampler`, `taper_adjust`, `taper_application`, `volume_adjust`,
`graph_cleaner`, `predict_volume`, `metric_projection` e `shape_to_raster` pertencem
a outras ferramentas do backend. Não são parâmetros do fluxo IFProcessing auditado.
`projeto` contém metadados; o nome do projeto DENDRO está em `_dendro.name`.

## Validação desta revisão

- Teste de processamento com os dez campos do inventário e as três referências do
  cadastro renomeadas, inclusive chaves diferentes nos dois arquivos: mesmo volume.
- Debug ativo acrescenta os cálculos; planilhas desmarcadas não são exportadas.
- A janela testa aplicação dos mapeamentos, confiança e sufixo; valor inválido
  não altera parcialmente o projeto.
- O cadastro de modelo testa abertura, cancelamento, salvamento e nova abertura
  com a janela principal corretamente restaurada.
- Nenhum config ou fonte da referência foi alterado; somente DENDRO foi editado.
