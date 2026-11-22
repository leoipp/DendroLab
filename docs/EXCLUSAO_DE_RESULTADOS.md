# Exclusão de resultados

A exclusão confirmada de dados incorporados ao projeto é imediata. Remover um conteúdo, uma tabela filha ou vários resultados elimina as entradas correspondentes do catálogo do DLB e atualiza as referências persistidas na mesma operação. Reabrir o projeto ou descartar outras alterações não restaura esses resultados.

Alterações não relacionadas que ainda estão em edição não são salvas implicitamente. A exclusão verifica a revisão dos metadados para rejeitar conflitos com outro salvamento sem remover dados ou referências.

O fluxo compartilhado atende Ativo biológico, aplicações de regressões, estratificação, geração de colunas, pareamento de medições, auditoria, séries Sentinel-2 e modelos de afilamento. Excluir uma aba preserva as demais abas do mesmo resultado e remove os resultados dependentes daquela aba. Excluir o último resultado de afilamento também limpa suas entradas no catálogo de ajustes.

Remover um resultado não remove sua entrada. Arquivos externos continuam preservados quando apenas sua referência é retirada de Conteúdos. A proteção das versões históricas de processamento continua em vigor.

Verificação automatizada: `--self-test-remove-discard <pasta>` (nome de comando mantido por compatibilidade). Inclui uma geração real de Ativo biológico, exclusões por conteúdo e por tabela, exclusão múltipla, abas irmãs, derivados, reabertura, Salvar como, preservação de alterações não salvas e rejeição de conflito de revisão.
