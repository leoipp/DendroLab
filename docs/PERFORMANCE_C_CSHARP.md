# Volumetria: comparação C e C#

Medição de 23/10/2026, neste computador: AMD Ryzen 5 3500, seis processadores
lógicos, Windows x64, .NET 10.0.11. C compilado pelo Zig 0.13.0/Clang com
`-O2`; C# em Release, com o JIT e as configurações padrão do runtime.

## Resultado observado

Medianas por lote, em duas execuções independentes:

| Árvores no lote | C: rodada 1 | C#: rodada 1 | C: rodada 2 | C#: rodada 2 | Razão C/C# |
|---:|---:|---:|---:|---:|---:|
| 1 | 0,000086 ms | 0,000045 ms | 0,000057 ms | 0,000027 ms | 1,94–2,13 |
| 10.000 | 1,086 ms | 0,426 ms | 1,618 ms | 0,675 ms | 2,40–2,55 |
| 1.000.000 | 68,648 ms | 25,466 ms | 125,910 ms | 47,331 ms | 2,66–2,70 |

**Neste cálculo isolado, C# foi aproximadamente 2,7 vezes mais rápido para
um milhão de árvores**, reduzindo o tempo em cerca de 62–63%. O erro relativo
máximo entre os resultados foi `3,497 × 10⁻¹⁵`, abaixo da tolerância `10⁻¹²`.

Os tempos absolutos variaram bastante entre as rodadas e entre amostras.
As razões acima descrevem este ensaio e esta compilação, não uma garantia
de velocidade em outras máquinas ou cargas.

## O que foi medido

- Mesma equação de Schumacher–Hall: `exp(b0+b1*log(DAP/10)+b2*log(H/10))*fator`.
- Mesmos arrays determinísticos de entrada e buffers de saída separados;
  resultados comparados árvore a árvore antes de medir.
- Uma thread por implementação. Aquecimento de pelo menos 800 ms para cada
  rotina e tamanho; 17 amostras, alternando a ordem C/C#.
- C chama a mesma `dl_volume` do motor atual. Inclui uma chamada P/Invoke
  por lote, com arrays fixados em memória; o laço e a matemática ficam em C.
- C# executa o laço equivalente com `Math.Log` e `Math.Exp`. Nenhum resultado
  é substituído por cache; ambas as rotinas preenchem seu buffer a cada lote.
- Para um ponto, cada amostra repete 20.000 chamadas; para 10.000 pontos,
  dez chamadas; para um milhão, uma chamada. A tabela normaliza por chamada.

Não houve alocação gerenciada por chamada após aquecimento em nenhuma das
rotinas. Os quatro arrays do ensaio de um milhão de árvores ocupam juntos
32.000.000 bytes. Esse valor é a estrutura do experimento, não o consumo de
RAM de um motor completo. Não foi instrumentada a heap nativa separadamente.

## Consequência para o DENDRO

Os números justificam experimentar a volumetria em C#. **Ainda não demonstram
que todo o processamento de inventário será 2,7 vezes mais rápido.** Leitura
de XLS/XLSX, junções de cadastro, estatísticas, RNA/HDF5, otimização de
regressões e gravação de relatórios não foram portadas nem comparadas aqui.
O custo de serializar projetos e tabelas na ponte JSON também ficou fora.

O motor usado pelo aplicativo continua em C. Antes de substituí-lo, precisamos
medir cada etapa relevante e comparar relatórios completos com as mesmas
entradas, parametrização, tolerâncias e critérios estatísticos. Também vale
comparar outras opções de compilação C; este resultado usa a compilação
atual, não estabelece um limite de desempenho da linguagem.


## Referência histórica após a migração

O aplicativo agora usa o núcleo C# de src/Dendro.Core. Este documento registra
o ensaio anterior da volumetria isolada, sem comprovar ganho no pipeline inteiro.
O código do comparador antigo está no ZIP .archive/DENDRO-C-reference-20261023.zip.
As medições atuais são executadas por scripts/benchmark.ps1 e gravadas em
.build/benchmarks/csharp-round-*.json, sem carregar qualquer DLL C.
