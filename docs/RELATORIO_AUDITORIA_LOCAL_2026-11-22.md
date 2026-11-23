# Relatório da auditoria local — 22/11/2026

## Estado das correções pós-auditoria — 22/11/2026

Foram aplicadas correções locais aos achados reproduzíveis. O resumo abaixo é o estado mais recente; as seções seguintes preservam o registro do diagnóstico inicial.

- **Estimativa estratificada:** resumo direto agora calcula erro padrão ponderado pela área dos estratos, estima variância dentro de cada estrato e usa graus de liberdade de Satterthwaite. A correção para população finita não é aplicada porque o sistema não conhece os totais populacionais por estrato; a compatibilidade com o delineamento adotado pela equipe ainda requer validação do responsável técnico.
- **GIS/CRS:** validação reconhece WKT1 e WKT2 e exige unidades lineares métricas; CRS em pés ou unidade desconhecida são rejeitados. Geometrias inválidas, vazias ou com área zero deixam de ser reparadas/descartadas silenciosamente; erros identificam a feição/registro.
- **Treinamento de modelos:** removidos o bridge Python, exportação temporária CSV, configuração do IFPC e teste de ACL associado. A UI usa implementação C# local para RNA, Random Forest, Gradient Boosting e SVR; pacotes gerados são carregados pelo Processador sem Python. Para bases acima de 250 mil linhas, o treinador percorre a tabela e usa amostra uniforme limitada; isso não equivale a ajuste em todas as linhas.
- **Interface:** mínimos de janela e central respeitam a área útil do monitor; o filtro de categoria recebeu nome de automação; ícones das ferramentas foram diferenciados. Testes de GIS, menus, resultados e layout foram atualizados para os fluxos atuais.
- **Falha fatal:** exceções não tratadas passam a ser registradas e encerram o app com código de falha, em vez de manter a sessão num estado potencialmente inconsistente.
- **CI:** workflow do instalador executa núcleo e WPF antes de empacotar, falha em caso de teste vermelho e publica logs/TRX como artefato de diagnóstico.
- **DLB:** novo formato `DNDRDB05` cifra o catálogo e os blocos com AES-256-GCM. Uma chave de dados aleatória por arquivo é protegida por chave derivada da senha via PBKDF2-HMAC-SHA-256 (600.000 iterações). DLB legado segue legível e é migrado ao salvar, com substituição atômica. Não há recuperação de senha.
- **Assinatura:** SignTool x64 foi localizado no Windows SDK; não há certificado de assinatura de código com chave privada nos repositórios de certificados verificados. Os artefatos ainda não são assinados e o atualizador ainda não autentica o publicador.

### Verificação após as correções

- `dotnet build src/Dendro.Desktop/Dendro.Desktop.csproj -c Release --no-restore`: passou após remover a ponte, **0 avisos e 0 erros**.
- `dotnet build DENDRO.slnx -c Release --no-restore`: passou, **0 avisos e 0 erros**.
- `dotnet test tests/Dendro.Core.Tests/Dendro.Core.Tests.csproj -c Release --no-restore`: **251/251 passaram**; os testes focados de criptografia DLB e treinamento nativo passaram **11/11**.
- `scripts/desktop.ps1 -Test -NoBuild`: passou. Self-test WPF integrado: **216 verificações**; workflow: **61**; duplicadas: **7**; exclusão persistente: **25**; menus: **104**.
- `--self-test-gis-contents`: **67 verificações aprovadas**; `--self-test-ui-results` e `--self-test-ui-layout` também passaram em execuções focadas.
- O DLB autorizado foi aberto somente para leitura: **938.106.302 bytes**, catálogo v2, **94 conjuntos** e **30.181.198 linhas**. A leitura completa conferiu os hashes de todos os blocos e interpretou os metadados do projeto; o arquivo original não foi alterado. Isso demonstra integridade estrutural, não validação científica dos resultados.
- A varredura de `ProcessStartInfo`/`Process.Start` não encontrou runtime Python/R nem subprocesso de computação no DendroLab. Permanecem dois usos de processo externos por propósito: abrir um link de copyright no navegador padrão e executar o instalador do atualizador após conferir o SHA-256 publicado no release. Esse hash detecta divergência no download, mas ainda não autentica quem publicou o release. `PickleData.cs` continua sendo um leitor C# inerte para modelos legados.
- O SignTool localizado é `C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe`. Sem certificado com chave privada, não foi possível assinar nem verificar identidade do publicador. A revisão do mecanismo de atualização e a integração do certificado ao pipeline ainda precisam ser concluídas.

### Onde Python é usado

O único arquivo Python que existia no repositório era `src/Dendro.Desktop/Scripts/model_fitter_bridge.py`; ele foi removido. A tela de aprendizado de máquina agora chama `NativeModelTrainer` em C# e grava ZIPs no formato nativo `dendro-native-v1`. Não exporta CSV e não inicia Python/IFPC.

O restante do DendroLab é C#/.NET. `PickleData.cs` interpreta estruturas de pickle legadas como dados inertes, sem carregar código executável ou iniciar runtime Python. A implementação nativa cobre os quatro tipos apresentados na UI. O treino é uma implementação própria e não promete paridade numérica com TensorFlow/scikit-learn. Tabelas acima de 250 mil linhas são percorridas e amostradas uniformemente até esse limite; validação científica em dados reais segue pendente.

**Diretriz confirmada pelo responsável do produto:** o treinamento de modelos deve ser implementado em C# no DendroLab, sem depender do IFPC nem de outro projeto externo. A ponte foi removida e o carregador nativo é exercitado nos quatro tipos de modelo. A varredura encontrou somente processos externos para abrir URLs no sistema operacional e iniciar o instalador do atualizador.

**Proteção do DLB:** usa primitivas criptográficas padrão do .NET (AES-GCM e PBKDF2), com formato/derivação de chaves próprios do DendroLab; não implementa cifra própria. A migração para o formato cifrado e os testes de autenticação foram implementados. A documentação descreve a perda irrecuperável caso a senha seja esquecida.

**Assinatura de releases:** o SignTool/Authenticode continua sendo a ferramenta escolhida. O binário está instalado, mas falta um certificado de assinatura de código com chave privada; por isso não foi possível assinar artefatos nem completar a autenticação do publicador no atualizador.

## Resumo executivo

A execução pontual foi feita no checkout local do DendroLab por três frentes de revisão: engenharia/segurança/performance, mensuração florestal/estatística/GIS e UI/UX. O produto é um aplicativo desktop WPF/.NET 10, com projetos e dados locais. Não é um SaaS web e não tem runtime de agentes LLM, backend multiusuário, fila ou serviço de execução recorrente.

**Resultado geral: requer revisão antes de confiar em todas as saídas científicas e nos fluxos de UI.** Foram confirmadas duas questões de alta prioridade em estimativa amostral e unidades de CRS, além de limitações de segurança, automação, interface e cobertura de testes. Nenhum projeto com dados de cliente foi aberto. As validações de cálculos e GIS usaram revisão de código e referências sintéticas existentes; não equivalem a certificação científica geral.

O checkout já tinha numerosas modificações locais não commitadas antes da auditoria. As falhas abaixo descrevem o estado observado; não foi possível compará-las com uma árvore limpa anterior para atribuir cada falha a uma alteração específica.

## O que foi executado

- Inspeção estática independente dos módulos por três revisores especializados; nenhum deles alterou código durante a revisão.
- `dotnet build DENDRO.slnx -c Release --no-restore`: **passou**, zero avisos e zero erros.
- `dotnet test tests/Dendro.Core.Tests/Dendro.Core.Tests.csproj -c Release --no-restore`: **232 passaram, 6 falharam, 238 total**.
- `dotnet list DENDRO.slnx package --vulnerable --include-transitive`: NuGet não reportou pacote vulnerável nos quatro projetos, usando a fonte atual. Isso não é uma análise de segurança do código nem garante ausência futura de CVEs.
- UI WPF: `--self-test-stacked-tabs` e `--self-test-chart-visuals` terminaram com código 0; `--self-test-remove-discard` passou 25 verificações.
- UI WPF com falhas: `--self-test-workflow` (elemento esperado não encontrado), `--self-test-gis-contents` e `--self-test-context-menu-icons` (busca `Single` sem correspondência), `--self-test-ui-results` (regra de ícones distintos), `--self-test-duplicates` (alinhamento do ícone de ajuda), `--self-test-ui-layout` (teste de simbologia vetorial antes de terminar) e `--self-test` completo (seleção por laço côncavo).
- `scripts/benchmark.ps1 -Rounds 1`: lote de volumetria C# medido em **0,000024 ms para 1 árvore, 0,235450 ms para 10 mil e 23,283100 ms para 1 milhão**, com zero bytes gerenciados alocados por lote após aquecimento. Mede somente `InventoryMath.VolumeBatch` (Schumacher–Hall + fator), sem I/O, GIS, RNA ou pipeline completo. Artefato: `.build/benchmarks/csharp-round-1.json`.

### Falhas do núcleo reproduzidas

Cinco falhas lançam `InvalidDataException: Coluna obrigatória ausente: Volume_m3_ha`:

- `DirectOutputColumnsTests.ProjectsEachLevelIndependentlyKeepsMandatoryColumnsAndDoesNotChangeTotals`.
- Dois casos de `InventoryIdentityTests`: deduplicação antes das estatísticas e processamento após exclusão de linhas.
- `InventoryModesTests.DirectTotalsWeightStandsByAreaAndSeparateYearsAndSelectedLevels`.
- `VolumeTypeTests.RegisteringVcccDoesNotReplaceVtccDuringCurrentProcessing`.

A sexta falha é `ParcelAreaValidationTests.ReportsEveryInvalidGeometryWithOriginalValuesAndOccurrenceCounts`: o teste espera o texto “declinações”, mas a validação retorna uma mensagem específica sobre declinação de comprimento. É necessária revisão para decidir se o comportamento ou a expectativa está incorreto.

### Falhas de UI e alcance do teste

- A suíte integrada `--self-test` falha em `DesktopTests.TestFreehandSelection`, na expectativa de que o laço côncavo respeite o contorno e ignore ponto vazio/removido (`DesktopTests.cs:1874`).
- `--self-test-ui-results` falha na asserção de ícones exclusivos (`DesktopTests.Results.cs:35`). O mapeamento atual reutiliza `Taper` em “Ajuste de taper” e “Aplicar taper”, e `Statistics` em “Testes estatísticos” e “K-means” (`MainWindow.xaml.cs:2174-2195`).
- `--self-test-ui-layout` falha em `DesktopTests.VectorSymbology.cs:33`, buscando um rótulo que não foi encontrado.
- Os demais erros de UI acima são falhas observadas no harness atual. Sem inspeção interativa adicional, não são tratados automaticamente como defeitos de produção reproduzidos.

## Achados priorizados

### Alta — incerteza amostral não segue o estimador ponderado

Em lote/projeto, `DirectInventorySummary` calcula o volume por hectare ponderando as médias de talhão pela área efetiva (`DirectInventorySummary.cs:227-234`), mas calcula o erro amostral usando todas as parcelas sem a mesma ponderação (`:238-240`) e aplica `t × s/√n` (`:320-325`). Com talhões de áreas diferentes ou alocação estratificada, esse intervalo pode não representar a incerteza do volume ponderado publicado.

**Ação sugerida:** revisar o estimador de variância conforme população-alvo, áreas, estratos e delineamento aceitos. Criar casos independentes com áreas distintas, alocação proporcional/desproporcional e correção para população finita quando aplicável. Até validar essa compatibilidade, interpretar com cautela o erro/IC desse resumo agregado.

### Alta — CRS projetado WKT2 em pés pode ser tratado como métrico

`ForestSampler.cs:87-93` reconhece `PROJCRS`, mas a checagem de unidades procura padrões `UNIT[...]` de WKT1. WKT2 representa a unidade linear como `LENGTHUNIT[...]`; CRS em pés podem passar sem conversão enquanto área, distâncias e buffers são consumidos como métricos (`ForestSampler.cs:19-22, 100-105`). A documentação do [PROJ](https://proj.org/en/stable/faq.html) descreve essa representação. O teste atual cobre um CRS métrico simplificado (`GrtsDesignTests.cs:35`), não o caso WKT2 em pés.

**Ação sugerida:** validar o fator da unidade linear via parser de CRS, cobrindo WKT1/WKT2 em metros/pés e CRS geográfico. Impedir cálculo de área/distância quando a unidade for desconhecida ou não convertida.

### Média — correção GIS silenciosa de geometrias

`ForestSampler.cs:100-103` tenta corrigir geometria inválida com `Buffer(0)` e descarta geometrias vazias sem registrar a feição nem eventual mudança de área.

**Ação sugerida:** incluir no resultado um aviso/evidência das geometrias reparadas ou descartadas e comparar a área antes/depois, preservando rastreabilidade.

### Média — CI não executa testes

O workflow observado compila/empacota, mas não roda `dotnet test` nem as suítes WPF (`.github/workflows/build-installer.yml:131`).

**Ação sugerida:** adicionar os testes automatizados ao CI e publicar artefatos/logs de falhas antes de gerar release.

### Média — atualizador sem validação de assinatura do editor

O atualizador obtém instalador e `SHA256SUMS.txt` do mesmo repositório e verifica o hash (`GitHubUpdates.cs:46`); depois inicia o instalador silenciosamente (`MainWindow.Updates.cs:45`). O hash detecta divergência dos bytes, mas não autentica de forma independente o editor se a conta/release for comprometida.

**Ação sugerida:** assinar o instalador e validar certificado/editor antes da execução silenciosa.

### Média — confidencialidade dos arquivos DLB depende do Windows

O formato DLB documenta compressão e checksum, não criptografia em repouso (`BANCO_DLB.md:40-47`). O checksum trata integridade, não sigilo.

**Ação sugerida:** definir classificação dos dados e modelo de ameaça; documentar ACLs, BitLocker/criptografia de disco e práticas de compartilhamento. Criptografia no formato deve ser avaliada separadamente por impacto em recuperação e compatibilidade.

### Média — ponte Python executa ambiente externo e exporta CSV temporário

O treinamento usa o `.venv\Scripts\python.exe` do ambiente IFPC indicado (`MachineLearningView.cs:251, 278`) e exporta a tabela para um arquivo temporário (`:290`). O executável Python pode rodar código com as permissões do usuário e o CSV existe fora do DLB durante a operação.

**Ação sugerida:** explicitar a fronteira de confiança, validar ambiente/modelos antes do uso e verificar ACLs/remoção segura do CSV temporário. ACLs reais e o IFPC instalado não foram inspecionados.

### Média — janela pode exceder a área útil em monitores pequenos

O `MainWindow` fixa mínimos de 1050×680 (`MainWindow.xaml:14-17`) e usa `Math.Max` ao limitar o tamanho pela área útil (`MainWindow.xaml.cs:103-105`). Em área útil menor que os mínimos, a janela pode ultrapassar a tela. A janela da central também exige mínimo 720×420 (`MainWindow.xaml.cs:1984-1987`).

**Ação sugerida:** dimensionar os mínimos pela área útil e verificar escala/DPI e resoluções menores.

### Baixa — filtro de categoria sem nome de automação explícito

`ToolCategoryFilter` tem tooltip, mas não `AutomationProperties.Name`/`LabeledBy` (`MainWindow.xaml:386-399`). As caixas de pesquisa próximas têm nomes explícitos.

**Ação sugerida:** fornecer nome acessível e verificar o fluxo com Narrator/UI Automation.

## Evidências positivas

- Credenciais NOAA/Copernicus são protegidas com DPAPI no perfil Windows (`WindowsCredentialStore.cs:89`).
- O DLB verifica blocos e rejeita inconsistências estruturais (`BANCO_DLB.md:47`).
- CI limita a permissão declarada a `contents: read` (`build-installer.yml:11`).
- Testes científicos sintéticos com contas fechadas cobrem alguns testes t/F/ANOVA/Brown–Forsythe (`ScientificTests.cs:12-80`); exemplos também verificam volumetria, área e Gini (`ManualCalculationExamplesTests.cs:14-18,43`).
- Documentação explicita hipóteses estatísticas; o teste de identidade não linear é descrito como aproximação (`ESTATISTICA.md`).
- O benchmark atual delimita a rotina medida e não atribui o ganho do microbenchmark ao pipeline inteiro (`PERFORMANCE_C_CSHARP.md:23`).

## Inventário de cobertura e limitações

| Área | Estado após esta execução |
| --- | --- |
| Build do núcleo, desktop e benchmarks | Validado — compilação concluída sem erros/avisos |
| Testes automatizados do núcleo | Reprovado — 232 aprovados, 6 falhas observadas |
| Algumas suítes WPF | Validado — abas empilhadas, visuais de gráfico e 25 verificações de exclusão |
| Outros fluxos WPF | Reprovado ou inconclusivo conforme as falhas descritas; sem varredura manual completa |
| Benchmark de volumetria em lote | Validado para o microbenchmark e máquina desta execução; não representa o app inteiro |
| Testes estatísticos sintéticos cobertos | Evidência existente; cobertura não generaliza a todos os delineamentos |
| Modelos em dados reais, RNA/IFPC, equações regionais, LiDAR e sensoriamento remoto | Não testado; sem dados autorizados e serviços configurados para esta execução |
| GIS em arquivos reais e CRS WKT2 em pés | Não testado; o risco WKT2 foi identificado por inspeção de código |
| Segurança de rede, ACLs, tráfego, credenciais reais e comportamento sob carga | Não verificável nesta auditoria local |
| UX com usuários, leitores de tela e dispositivos pequenos | Não testado; riscos de layout/nome acessível identificados por inspeção |
| Operação SaaS, RBAC multiusuário e agentes LLM 24/7 | Não aplicável ao checkout desktop atual; essa infraestrutura não existe |

## Sistema de auditoria adicionado ao checkout

Foi adicionada uma central local que apresenta o catálogo inicial e registra o reconhecimento do catálogo. Ela não executa os módulos nem valida cálculos. O ledger local de achados/recomendações é separado dos projetos `.dlb`. A central abre por botão no ribbon e não exige abrir um projeto. O fluxo de inferência por IA, a execução automática de testes e as decisões de aprovação ainda não estão implementados.

## Próximas diretrizes recomendadas

1. Priorizar a revisão científica do estimador amostral ponderado e o bloqueio/validação de unidades CRS WKT2.
2. Resolver as seis falhas de núcleo e corrigir o harness WPF até que os fluxos possam ser classificados com evidência confiável.
3. Incluir testes na CI antes da geração de release.
4. Definir política de dados, assinatura de instalador e fronteira de execução Python/arquivos temporários.
5. Planejar responsividade e acessibilidade do app desktop.
6. Só então decidir se vale investir em um serviço LLM ou manter as auditorias como execução local supervisionada.
