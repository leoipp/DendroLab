# Análise dos cinco vídeos de UI/UX e propostas para o DendroLab

Data: 17/11/2026. Escopo: análise e proposta; nenhuma nova alteração na interface nesta etapa.

## Remoção solicitada da página inicial

Por solicitação do usuário, a página inicial, o botão Início e sua abertura automática foram removidos. O aplicativo volta a abrir diretamente na área de trabalho. As referências à página inicial abaixo descrevem a proposta histórica e não o estado atual.

## Aplicação posterior das propostas

Padronização posterior solicitada: o Processador usa botão “Executar” fixo no canto inferior direito; o campo de prefixo foi retirado, preservando a geração de nomes disponíveis; seleções de inventário aparecem em etiquetas removíveis com quantidade no seletor. As ajudas são círculos de 20 DIPs, posicionados na linha do rótulo; foram retiradas as ajudas da base e do checkbox Por projeto, e a explicação dos níveis foi movida para Saídas do resumo direto. K-means, validação de modelos, auditoria, testes estatísticos, treinamento de modelos, fronteira, ICA/IMA e pareamento compartilham um formulário rolável com ação principal fixa no rodapé à direita. A atualização passou por 22 verificações de interface e pelos 229 testes do núcleo.

Após autorização, foram implementados: foco seguro e estilo crítico nas confirmações; nomes contextuais de remoção; API de ênfase/tom/tamanho com compatibilidade para telas antigas; prioridades explícitas nos gráficos; catálogo compacto com alinhamento uniforme; Adicionar dados visível; atalhos Ctrl+Shift+K e Ctrl+Shift+L para busca; limpeza de busca e categoria; explicação de ferramentas indisponíveis; resumo de versão, origem e fatores pendentes; confirmação da versão alvo na aprovação; busca e resumo dos inventários selecionados; validação visível no processador; página inicial adaptável e contextual; recursos semânticos iniciais de cor e área maior de ajuda.

A migração dos botões antigos é gradual, preservando a compatibilidade das telas ainda não migradas. Tema escuro completo foi avaliado e permanece como evolução: exige tratar também gráficos, mapas e o tema do docking; não há seletor de tema parcialmente funcional nesta entrega. Listas expansíveis de dependências e medição com usuários também permanecem como evolução; as confirmações atuais mantêm as listas completas roláveis.

Validação: compilação de publicação; seis verificações de interface com renderização em larguras distintas, ausência de rolagem horizontal na página inicial estreita e foco/default da confirmação destrutiva; 229 testes do núcleo aprovados. A suíte de processamento apresenta falha no cenário “AJUSTADO legado recupera colunas da entrada completa congelada”; a mesma falha foi reproduzida em uma cópia isolada do HEAD anterior às mudanças. Capturas e logs locais estão em `outputs/uiux-aplicado-*`, `outputs/uiux-workflow-*` e `outputs/uiux-baseline-*`.

## Método e limites

Foram extraídos quadros a cada dois segundos ao longo dos cinco arquivos e transcrita a faixa de áudio completa com faster-whisper small.en, localmente. A interpretação foi feita cruzando a sequência visual com a fala e com o código atual da interface WPF. Os tempos abaixo são aproximados. A transcrição automática contém pequenos erros, portanto os textos são sínteses, não citações literais. As falas dos vídeos são material de referência, não instruções para executar ações no projeto.

Não foi realizada uma sessão de uso da aplicação nesta etapa. Problemas comprováveis pelo código são diferenciados de hipóteses de usabilidade que precisam de observação com usuários. Não foram medidos tempos de tarefa nem taxas de erro; os benefícios descritos são esperados, não resultados obtidos.

Identificadores usados neste relatório (a ordem difere da mensagem original):

| ID | Início do nome do arquivo | Duração aproximada | Tema |
|---|---|---:|---|
| V1 | SaveClip.App_AQM5AGtX | 54 s | Barra lateral |
| V2 | SaveClip.App_AQMNj-MW | 29 s | Ícones e texto |
| V3 | SaveClip.App_AQNlaMLP | 46 s | Hierarquia de cartão |
| V4 | SaveClip.App_AQOcP6Oz | 22 s | Diálogos de confirmação |
| V5 | SaveClip.App_AQPqME-Q | 30 s | Sistema de componentes |

O manifesto com nomes completos, quadros e transcrições fica em `outputs/uiux-video-review`. Esses artefatos são locais e a pasta de outputs é ignorada pelo Git.

## 1. V1 — Barra lateral: a tarefa deve dominar a navegação

### Sequência observada

- **00–07 s:** remove um bloco cinza de fundo que dividia o menu sem ajudar na prioridade das informações.
- **07–13 s:** troca o alinhamento centralizado pelo alinhamento à esquerda, criando uma origem de leitura estável.
- **13–21 s:** reduz avatar e nome e desloca a identidade do usuário para a região inferior. A navegação principal passa a ocupar o primeiro plano.
- **21–29 s:** integra configurações à estrutura e separa navegação frequente de configurações e suporte. A fala sugere até cinco itens no grupo principal.
- **29–34 s:** transforma a busca em um campo que ocupa a largura útil e apresenta uma indicação de atalho.
- **34–44 s:** regulariza espaçamento, área de interação, peso do texto, hover e seleção ativa.
- **44–49 s:** desloca o contador à direita e muda sua cor para melhorar a leitura.
- **49–54 s:** reforça margens, identidade do produto e uma versão recolhida do menu.

### Aplicação ao DendroLab

O app já tem cinco categorias de ferramentas, busca, painéis encaixáveis, ocultação automática e recolhimento do ribbon com Ctrl+F1. Essas capacidades são uma base útil. O catálogo, entretanto, mostra dezenas de ferramentas como cartões com moldura e caixa de ícone; isso faz cada linha competir visualmente pela atenção (`ShellStyles.xaml:259` e `348`, `MainWindow.xaml.cs:1862`).

**Proposta:** apresentar o catálogo como linhas compactas, com ícone, nome, seleção e uma descrição curta apenas quando necessária. Reservar molduras para agrupamentos com conteúdo próprio. Manter as cinco categorias atuais e permitir expandi-las ou filtrá-las. Preservar busca global pelas ferramentas, mesmo quando um grupo estiver recolhido.

**Proposta:** distinguir categoria selecionada, ferramenta aberta e documento ativo. São três estados diferentes: filtro da lista, formulário de trabalho e aba central. Um único destaque genérico não comunica os três. Ao abrir uma ferramenta, manter seu título e uma ação de retorno com texto acessível.

**Proposta:** adicionar uma ação visível “Adicionar dados” em Conteúdos. Arrastar arquivos e menus de contexto continuam úteis, mas precisam ter uma alternativa fácil de descobrir para quem começa. A busca deve ter um rótulo persistente; um atalho só deve aparecer quando existir e não conflitar com edição de tabelas.

**Proposta:** preservar posições e escolhas ao alternar entre a lista e um formulário. A busca atual já evita substituir um editor aberto; vale manter esse comportamento.

**Cuidado de interpretação:** cinco itens é uma heurística apresentada no vídeo, não uma regra universal. Não limitar a árvore de arquivos a cinco entradas nem esconder ferramentas importantes em um menu genérico. Contadores de itens comuns devem ter tom neutro; vermelho deve representar um problema real ou ação destrutiva, não qualquer quantidade.

## 2. V2 — Ícones e texto: alinhamento resulta de uma estrutura comum

### Sequência observada

- **00–08 s:** compara ícones grandes demais e pequenos demais em uma lista.
- **08–17 s:** usa texto de 14 com altura de linha de 20 e ícones em caixas de 20 × 20, alinhados ao centro da linha.
- **17–23 s:** alinha as caixas dos ícones à mesma guia vertical.
- **23–27 s:** aplica intervalo de 8 entre ícone e texto.
- **27–29 s:** refina padding vertical e cores.

### Aplicação ao DendroLab

No `ToolCard`, o ícone ocupa 20 × 20 dentro de uma caixa de 32 × 34, mas o texto pode ser precedido por um `HelpIcon`. Quando há ajuda, o nome começa em outra posição horizontal; quando não há, o HelpIcon fica colapsado (`ShellStyles.xaml:274`, `HelpIcon.cs`). Isso é uma inconsistência estrutural verificável. Títulos com várias linhas também precisam de uma decisão consistente sobre alinhamento do ícone.

**Proposta:** criar uma linha compartilhada com colunas explícitas: ícone de 20, intervalo de 8, título flexível e ação auxiliar. Levar o ícone de ajuda para o final ou colocar a explicação em uma segunda linha. Assim o nome não muda de coluna conforme a presença de ajuda.

**Proposta:** padronizar texto de navegação em 13–14 DIPs e altura de linha em 20 DIPs, com ícones de 20 DIPs. Para árvores densas, aceitar uma variante de 16 DIPs. O tamanho do desenho não deve definir sozinho o tamanho clicável.

**Proposta:** separar a caixa visual da área de interação. O HelpIcon atual tem 16 × 16; manter um desenho pequeno dentro de um alvo maior pode facilitar uso sem aumentar ruído. Validar mouse, teclado e eventual uso por toque.

**Cuidado de interpretação:** WPF mede layout em DIPs; os valores de pixel do vídeo não devem ser copiados como pixels físicos. Conferir 100%, 125%, 150% e 200% de escala. Ajustes ópticos pequenos são legítimos, pois dois desenhos em caixas iguais podem ter pesos visuais diferentes. Não forçar ícones de ribbon vertical a seguir o mesmo arranjo das linhas de navegação.

## 3. V3 — Hierarquia: proximidade e prioridade antes da decoração

### Sequência observada

- **00–09 s:** experimenta ampliar uma foto e discute a relação entre cantos internos e externos.
- **09–16 s:** reduz a imagem para liberar espaço e alinha o texto à esquerda.
- **16–24 s:** diferencia título e informação secundária por peso e cor e aproxima elementos relacionados.
- **24–30 s:** escurece a descrição e a torna mais curta.
- **30–36 s:** aproxima os rótulos de suas métricas e inclui uma nota numérica comparável, em vez de depender apenas de estrelas.
- **36–42 s:** diferencia ação principal e secundária e dispõe as ações lado a lado.
- **42–46 s:** harmoniza cantos e mostra uma variação visual final.

### Aplicação ao DendroLab

**Gráficos:** “Gerar / atualizar gráfico” e “Gerar gráficos em lote” começam com “Gerar”, logo ambos recebem destaque primário pelo helper (`ChartView.cs:109–135`, `Ui.cs:241`). “Ajustar este gráfico” segue outro tratamento. Propõe-se um primário para a etapa atual: antes de existir gráfico, “Gerar gráfico”; depois, “Atualizar gráfico”, com “Ajustar modelo” como próxima ação clara. Geração em lote deve ser secundária. O fluxo exato deve ser validado com quem usa ajustes repetidamente.

**Revisão de inventário:** o status é hoje um TextBlock com tipo, versão, autor, datas e aprovação (`ProcessingReviewView.cs:102`). Transformá-lo em um resumo: versão e situação em primeiro nível; base de origem logo abaixo; autor e datas como metadados. Aproximação de rótulo e valor ajuda a distinguir “aprovado histórico” de “aprovado vigente”. Um selo de status deve conter texto, não somente cor.

**Revisão e aprovação:** agrupar “Revisar fatores”, “Criar versão corrigida” e “Aprovar versão” por etapa e estado. O próprio código alerta que aprovar a versão selecionada não publica fatores editados. Essa distinção merece texto visível junto da ação e um resumo de alterações pendentes; deixá-la apenas na ajuda pode exigir que o usuário descubra a regra depois de agir. Não bloquear aprovações legítimas sem entender o fluxo operacional.

**Página inicial criada anteriormente:** retirar os números 01/02 de Criar/Abrir: são alternativas. Manter um título funcional e reduzir texto introdutório. A página deve adaptar dois cartões e três passos a uma coluna quando a área central encolher; atualmente são grids com quantidades fixas de colunas (`WelcomeView.cs:73` e `113`). Quando há projeto aberto, uma futura variante deve mostrar o projeto, as origens e o próximo passo contextual; isso é uma proposta nova, ainda não implementada.

**Métricas científicas:** em resultados, apresentar rótulo e valor próximos, com unidade e precisão coerentes. Um número isolado, uma estrela ou um selo de “melhor” não explica qualidade de ajuste. R², erro e tamanho da amostra precisam de contexto e de valores observados verificáveis. Não reduzir tabelas científicas a cartões quando a comparação entre linhas e colunas é a tarefa principal.

**Cuidado de interpretação:** a demonstração de aumentar e depois reduzir a foto é uma exploração, não duas regras simultâneas. A lição é escolher o espaço conforme a função. Colocar botões lado a lado também depende da largura e dos rótulos em português; empilhar é adequado quando não cabem. Modo escuro aparece como apresentação final, não como requisito do produto.

## 4. V4 — Confirmações: ação, objeto e consequência

### Sequência observada

- **00–07 s:** troca alinhamento central por duas âncoras consistentes: conteúdo à esquerda, ações à direita.
- **07–12 s:** substitui “Tem certeza?” por verbo e objeto, por exemplo “Excluir arquivo?”.
- **12–18 s:** inclui fechamento, organiza Cancelar antes da ação destrutiva e evita caixa alta.
- **18–22 s:** identifica o objeto no corpo e descreve o efeito da ação; mostra exemplos para arquivo e membro de equipe.

### O que o app já faz bem

`Ui.ShowMessage` já alinha o conteúdo à esquerda e as ações à direita, usa Cancelar antes da confirmação e aceita rótulos explícitos. Há confirmações que identificam o gráfico, análise ou revisão. O título nativo da janela já oferece fechamento; não é necessário adicionar outro X apenas para reproduzir o vídeo.

### Lacunas e propostas

1. **Foco e Enter:** `Ui.cs:345` define a confirmação como botão padrão e `Ui.cs:389` dá foco a esse botão inclusive em exclusões. Propõe-se foco inicial em Cancelar para ações destrutivas, Esc cancela e Enter confirma somente quando a ação destrutiva estiver deliberadamente selecionada. Essa é uma extensão proposta para o app, não uma fala específica do vídeo.
2. **Rótulos:** substituir “Remover” por “Remover gráfico”, “Remover análise” ou “Remover 3 conteúdos” nos respectivos contextos. O helper genérico `ConfirmRemoval` usa “Confirmar remoção” e deve receber uma descrição contextual da operação.
3. **Consequência correta:** distinguir retirar uma referência externa do projeto, excluir dados incorporados ao DLB, remover derivados e descartar alterações ainda não salvas. Não usar “permanentemente” ou “não pode ser desfeito” sem corresponder ao mecanismo real de recuperação.
4. **Dependências:** antes de remover conteúdo com gráficos ou análises dependentes, apresentar resumo com quantidade e lista expandível. Para listas longas, manter consequência e ações visíveis enquanto a lista rola.
5. **Estados:** ação destrutiva com variante e tom explícitos, incluindo hover e foco. Hoje `ShowMessage` define cores locais sobre o template do botão genérico; os gatilhos de hover desse template usam cores azuis. O diálogo precisa conservar coerência de tom em todos os estados.

Exemplo de redação para uma remoção de referência externa, condicionado à regra real:

> **Remover “Inventário 2026.xlsx” do projeto?**
>
> O arquivo original será mantido. [Resumo exato dos gráficos e análises que também serão removidos, conforme a operação.]
>
> Cancelar · Remover do projeto

Exemplo de publicação de revisão:

> **Criar versão corrigida?**
>
> Será criada uma nova versão a partir de [versão/base], com [quantidade] fatores alterados. [Explicar corretamente o status resultante e a preservação da origem.]
>
> Cancelar · Criar versão corrigida

Os campos entre colchetes devem ser preenchidos com dados reais; não são texto final para a interface.

## 5. V5 — Componentes: ênfase, significado e tamanho são dimensões diferentes

### Sequência observada

- **00–04 s:** mostra uma lista de variantes que mistura prioridade visual e significado destrutivo.
- **04–09 s:** separa variante (primária/secundária/terciária), tom (neutro/crítico) e tamanho.
- **09–16 s:** demonstra mudar a ênfase sem trocar o componente nem o rótulo.
- **16–23 s:** combina tom crítico com diferentes níveis de ênfase.
- **23–30 s:** combina tamanho com as demais propriedades e mostra a correspondência no código.

### Aplicação ao DendroLab

`Ui.CreateActionButton` deduz a aparência pelo início da string. “Gerar”, “Aplicar”, “Salvar”, “Comparar” e vários outros verbos tornam o botão primário; apenas duas famílias de rótulos de remoção são reconhecidas como perigosas (`Ui.cs:219–259`). Consequências verificáveis: renomear ou traduzir um botão pode mudar sua aparência, e dois comandos da mesma tela podem ganhar a mesma prioridade sem decisão explícita de design.

**Proposta estrutural:** declarar propriedades independentes na criação do botão:

```csharp
// API proposta; ainda não existe no projeto.
Ui.Button("Gerar gráfico", onGenerate,
    emphasis: ActionEmphasis.Primary,
    tone: ActionTone.Neutral,
    size: ActionSize.Medium);

Ui.Button("Remover gráfico", onRemove,
    emphasis: ActionEmphasis.Secondary,
    tone: ActionTone.Critical,
    size: ActionSize.Small);
```

Manter uma adaptação temporária para chamadas antigas e migrar tela por tela. A mudança do helper exige levantamento dos usos; trocar o comportamento global de uma só vez pode alterar muitos fluxos inesperadamente.

**Proposta visual:** usar recursos semânticos compartilhados para texto principal, texto secundário, superfície, borda, seleção, foco e tom crítico. Hoje as cores estão espalhadas entre XAML, helpers e views; a página inicial também introduz seus próprios verdes. Consolidar a paleta permite mudar um valor com previsibilidade e torna um futuro tema escuro viável, inclusive para AvalonDock. Tema escuro deve ser uma etapa posterior à consistência do tema atual.

**Estados mínimos do componente:** normal, hover, pressionado, foco de teclado, desabilitado e ocupado. Em execução assíncrona, indicar andamento no comando/contexto, além da barra global, e explicar falhas de forma acionável. Mostrar a indisponibilidade de uma ferramenta junto do requisito que falta, por exemplo “Crie um gráfico para ajustar uma regressão”.

## 6. Direção visual e funcional proposta

O produto é uma estação de análise desktop com tabelas, gráficos e mapas. A proposta deve priorizar espaço de trabalho, leitura científica e descoberta das ações.

| Elemento | Direção proposta |
|---|---|
| Conteúdos | Árvore com origem e derivados, busca e Adicionar dados visível |
| Ferramentas | Categorias e linhas compactas; ao abrir, formulário com título e retorno |
| Área central | Tabela, gráfico ou mapa ocupando o maior espaço disponível |
| Ribbon | Ações de projeto e navegação agrupadas; configuração, ajuda e atualização com menor prioridade |
| Status | Andamento global; validação e próximo passo também no contexto local |
| Resultados | Métricas agrupadas com unidades, versão e origem; tabelas quando a comparação exigir |
| Confirmações | Objeto, consequência, dependências e ação nomeada |

A janela mínima declarada é 1050 DIPs; os painéis iniciais usam 245 e 310 DIPs e o ribbon 140 de altura (`MainWindow.xaml`). Isso deixa cerca de 495 DIPs centrais antes de bordas, divisores e chrome. É um indício concreto para priorizar adaptação por largura disponível. Os valores são de layout, não uma medição da janela renderizada.

Como ponto de partida para um sistema, propor espaçamentos de 4/8/12/16/24/32 DIPs; texto de corpo 13–14, subtítulos 16–18, títulos de página 22–26 e altura de linha de 20 para listas. Esses valores precisam ser avaliados em tabelas densas, títulos longos e escala do Windows. Raio de canto deve acompanhar o tamanho do componente; não é necessário usar o mesmo raio em tudo.

Foi calculado o contraste nominal de algumas cores do código pela luminância relativa sRGB: `#788B9A` sobre branco dá aproximadamente **3,52:1**; `#26465F` sobre branco, **9,89:1**; branco sobre `#21669D`, **6,08:1**. O primeiro aparece em textos pequenos como RibbonGroupLabel e merece revisão. Esses cálculos não medem a tela renderizada: fundo real, opacidade, hover e estado desabilitado precisam ser considerados antes de concluir sobre acessibilidade.

## 7. Backlog priorizado

P0: prevenir ação indesejada ou confusão sobre dados. P1: melhorar tarefas frequentes. P2: refinamento e expansão. Esforço relativo: pequeno, médio ou grande; não representa orçamento em horas.

| Ordem | Prioridade | Mudança | Evidência | Esforço | Critério de aceite |
|---:|---|---|---|---|---|
| 1 | P0 | Foco seguro nas confirmações destrutivas | Ui.cs:345/389 | Pequeno | Abrir diálogo e pressionar Enter não executa exclusão por padrão; Esc cancela |
| 2 | P0 | Nomear objeto e efeito de remoção | ConfirmRemoval e chamadas | Médio | Pessoa consegue dizer o que sai do projeto e o que acontece ao arquivo original |
| 3 | P0 | Expor a diferença entre editar fatores, criar versão e aprovar | ProcessingReviewView | Médio | Edições pendentes e versão alvo ficam claras antes da aprovação |
| 4 | P1 | API explícita de ênfase/tom/tamanho | V5; Ui.cs:219 | Médio | Mudar o rótulo não muda estilo; estados visuais coerentes |
| 5 | P1 | Definir ação primária por etapa nos gráficos | V3; ChartView | Pequeno | Uma ação dominante no grupo; lote tratado como alternativa |
| 6 | P1 | Unificar linha de ícone e título | V2; ToolCard | Pequeno | Títulos com/sem ajuda começam na mesma guia |
| 7 | P1 | Simplificar catálogo de ferramentas | V1; ToolCardItem | Médio | Reduzir decoração mantendo nomes, foco, seleção e busca legíveis |
| 8 | P1 | Adicionar dados com comando visível | V1; Conteúdos | Pequeno | Primeiro arquivo pode ser adicionado sem descobrir menu de contexto ou arrastar |
| 9 | P1 | Resumo estruturado de versão e aprovação | V3; status de revisão | Médio | Versão vigente identificável sem interpretar um parágrafo |
| 10 | P1 | Validação visível nos campos | ProcessorView.TryCapture | Médio | Valor inválido apresenta mensagem junto do campo; não depende apenas de tooltip |
| 11 | P1 | Seleção de inventários mais inspecionável | ProcessorView.inventoryMenu | Médio | Mostrar seleção, busca e quantidade; avaliar lista/popover adequado para muitas bases |
| 12 | P1 | Reorganizar a página inicial | WelcomeView | Médio | Alternativas sem numeração sequencial; leitura adequada com área central estreita |
| 13 | P1 | Explicar ferramentas indisponíveis | ShowTools; RefreshEnabled | Pequeno | Requisito faltante acessível por mouse e teclado |
| 14 | P1 | Consolidar tokens semânticos | Controls, ShellStyles, views | Médio | Estados e cores comuns deixam de depender de valores locais repetidos |
| 15 | P2 | Melhorar alvos de ajuda e ícones | HelpIcon; Ui.IconButton | Pequeno | Área de interação confortável preservando o desenho compacto |
| 16 | P2 | Busca com atalho e limpeza visível | V1 | Médio | Atalho funcional, sem interferir na edição de células |
| 17 | P2 | Página inicial contextual com projeto aberto | V3 | Médio | Identificar projeto e próximo passo sem recompor dados desnecessariamente |
| 18 | P2 | Avaliar tema escuro completo | Finais dos vídeos; tokens | Grande | Gráficos, mapas, menus, docking e foco funcionam como um conjunto |

## 8. Sequência sugerida de execução e avaliação

**Primeira entrega:** confirmações, semântica dos botões e correção das prioridades nos gráficos. São melhorias localizadas com impacto direto em ações e dados.

**Segunda entrega:** catálogo compacto, alinhamento de ícones, Adicionar dados visível e adaptação da página inicial. Avaliar o layout mínimo antes de expandir o redesign.

**Terceira entrega:** revisão de inventário com resumo de versão, alterações pendentes e fluxo de aprovação; processamento com seleção e validação mais visíveis.

**Quarta entrega:** consolidar tokens e avaliar preferências de densidade/tema conforme demanda. Parte dos tokens deve nascer junto dos componentes das primeiras entregas; a consolidação ampla vem depois.

Para avaliar, comparar a interface atual com a proposta nas mesmas tarefas e com os mesmos dados: criar projeto e adicionar primeira tabela; localizar e executar processamento; criar gráfico e ajustar modelo; corrigir fatores e aprovar a versão certa; remover referência e explicar a consequência. Registrar tempo, cliques, erros, pedidos de ajuda e entendimento da operação. Observar iniciantes e usuários experientes, com rodadas curtas de melhoria. Não estabelecer uma promessa percentual de ganho sem linha de base.

Cobrir navegação por Tab/Shift+Tab, foco visível, Enter/Esc, seleção e edição de tabelas, nomes longos em português, filtros sem resultado, ausência de gráficos, muitas origens, operação em andamento e erro. Verificar a menor janela permitida, painéis redimensionados/ocultos e escala de Windows de 100% a 200%. Calcular contrastes dos estados reais antes de afirmar conformidade de acessibilidade.

## 9. Conclusão de projeto

Os cinco vídeos convergem em decisões de estrutura: alinhar, agrupar, aproximar, declarar prioridade e tornar a consequência explícita. Para o DendroLab, os maiores ganhos potenciais estão nos componentes compartilhados e nos fluxos de análise/revisão. A página inicial é apenas um dos pontos de contato; o redesenho deve acompanhar quem já está trabalhando com seus dados.
