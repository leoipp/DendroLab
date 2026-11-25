# Diagnóstico e plano do sistema de auditoria

## Escopo reconhecido

O repositório contém o **DendroLab**, um aplicativo desktop Windows, não um SaaS web. A solução usa C#/.NET 10, interface WPF, persistência local de projetos `.dlb` e bancos LiteDB, com cálculos no `Dendro.Core`. Não foram encontrados serviço backend, API de produto, autenticação multiusuário, isolamento de tenants, fila, worker, scheduler de serviço, telemetria centralizada ou integração com provedor de modelos de linguagem.

O app registra erros não tratados em `%LOCALAPPDATA%\DENDRO\errors.log`, consulta atualizações no GitHub Releases e acessa serviços de dados de mapas/satélite em fluxos específicos. Isso não constitui uma plataforma de observabilidade nem um runtime de agentes. Os temporizadores WPF existentes operam enquanto a janela está aberta e não oferecem execução contínua 24/7.

Os projetos contêm alterações locais não commitadas anteriores a este trabalho. Elas foram preservadas; a implementação nova está em arquivos próprios.

## Arquitetura e áreas funcionais identificadas

| Área | Componentes encontrados | Criticidade inicial | Estado desta auditoria |
| --- | --- | --- | --- |
| Aplicativo e interface | WPF, AvalonDock, ribbon, janelas e painéis | Alta | Testes focados de resultados, layout, GIS e menus passaram; self-test integrado ainda falha em `DesktopTests.cs:757` |
| Projeto e persistência | Arquivos DLB, metadados JSON, armazenamento LiteDB e histórico versionado | Crítica para integridade local | Testes sintéticos do núcleo passaram; cobertura geral e formatos externos ainda requerem validação |
| Importação e tabelas | Excel, XLS legado, tabelas DLB, filtros, junções, edição e exportação | Alta | Identificada; cobertura existente no projeto, sem execução nesta etapa |
| Inventário | Processamento IFC/IFPC, identidade, validações, auditoria e saídas | Crítica científica/operacional | Identificada; precisa de validação independente por algoritmo |
| Mensuração e modelos | Volumetria, taper, regressões, RNA, validação e modelos de crescimento | Alta | Identificada; referências e testes existentes, resultado geral não revalidado |
| Estatística e amostragem | Análises, testes, clusters, isolamento, GRTS e planejamento de campo | Alta | Identificada; documentação e testes existem, cobertura total não medida |
| GIS e sensoriamento remoto | SHP, simbologia, amostragem, NOAA e Sentinel-2 | Alta | Identificada; integrações/CRS e desempenho não revalidados |
| Atualização e serviços externos | GitHub Releases, NOAA, Copernicus e mapas base | Média/alta | Identificada; disponibilidade e credenciais não verificadas |
| Segurança e privacidade | Credenciais do Windows, arquivos locais, rede e dependências | Crítica | Não verificável integralmente sem threat model, configuração e revisão dedicada |
| Observabilidade | Log local de exceções e indicadores transitórios da UI | Média | Lacuna identificada; não há métricas históricas centralizadas |

Os módulos e as funções acima são inventário do código/documentação, não confirmação de que cada fluxo funciona. Os status de teste seguem deliberadamente como **Não testado** ou **Não verificável** onde não houve execução ou evidência suficiente.

## Dependências para atender ao prompt mestre

1. **Execução 24/7:** exige hospedar um serviço persistente fora do ciclo de vida da janela WPF, além de definir máquina/conta, operação, atualizações e recuperação.
2. **Agentes com inferência:** exige provedor/modelo aprovado, credenciais, limites de custo, política de retenção e regras de envio de dados. Nenhum provedor foi identificado/configurado.
3. **Auditoria de interface remota:** o produto atual é desktop; Playwright não controla WPF. A automação deverá usar UI Automation/Windows em máquina de homologação, ou a aplicação terá de ganhar uma interface web.
4. **Dados e ciência:** não usar projetos reais por padrão. Casos sintéticos e referências existentes podem formar o conjunto inicial; tolerâncias científicas precisam ser definidas por método e unidade.
5. **Painel administrativo:** não existem contas nem papel administrador no app. A primeira versão pode ser local e restrita ao usuário Windows; aprovação registrada localmente não equivale a RBAC de um serviço multiusuário.
6. **Produção:** não existe ambiente de produção/deploy de SaaS neste repositório. A regra de não alterar produção é atendida pela ausência de qualquer integração de escrita externa.

## Base local entregue

As auditorias do produto são executadas fora do aplicativo durante o desenvolvimento e a revisão. Não há central, catálogo de auditoria ou ledger local exposto ao usuário; o DendroLab deve apresentar funcionalidades prontas para uso. Agentes de auditoria, tarefas recorrentes e decisões de aprovação pertencem ao fluxo externo de desenvolvimento, não ao app distribuído.

## Plano incremental compatível

### Fase 1 — diagnóstico

Concluída no nível de inspeção do repositório. A cobertura detalhada por fluxo e a revisão de segurança continuam pendentes.

### Fase 2 — registro e governança local

Ledger estruturado, catálogo inicial, execução manual de reconhecimento e UI local de consulta implementados. Testes focados cobrem persistência, histórico, validação de entradas e a distinção entre reconhecimento e teste de produto. Fluxos de aprovação pela UI e execução reproduzível de testes continuam pendentes.

### Fase 3 — auditoria determinística

Executar primeiro testes existentes e verificações com dados sintéticos; ligar versões, commit, ambiente, entradas, tolerância e resultado ao ledger. Implementar referência independente por método científico, sem inferir que toda metodologia válida deve produzir os mesmos resultados.

### Fase 4 — interface e UX

Adicionar inspeções locais de layout/acessibilidade e automação UI Automation numa VM de homologação. Não apresentar uma pontuação automática como avaliação completa de UX.

### Fase 5 — agentes de IA supervisionados

Só após escolha/configuração de provedor, política de dados e orçamento. Agentes poderão resumir evidências autorizadas e criar recomendações; execução de código e ações externas permanecerão desabilitadas por padrão.

### Fase 6 — serviço contínuo

Só é tecnicamente possível após decisão de hospedar um serviço separado. Definir plataforma, identidade, scheduler, fila, banco compartilhado, observabilidade, segredos, backups, recuperação e aprovações. O app WPF não será mantido aberto como substituto de um worker.

## Validação desta entrega

`dotnet build DENDRO.slnx -c Release --no-restore` concluiu sem avisos ou erros; os testes do núcleo passaram **246/246**. `scripts/desktop.ps1 -Test` passou: self-test WPF integrado (216 verificações), workflow (61), duplicadas (7), exclusão persistente (25), menus (104) e ACL da ponte Python (4). Os testes focados GIS (67), resultados e layout também passaram. O DLB autorizado foi lido sem escrita: catálogo v2, 94 conjuntos, 30.181.198 linhas e hashes de todos os blocos verificados. O IFPC local importa `ModelFitter.py` e `pip check` está limpo após restringir protobuf para `<7`; não foi executado treinamento. Permanecem fora desta validação a assinatura do atualizador, a política de criptografia do DLB, compatibilidade científica ampla, credenciais/segredos de CI e integrações externas.
