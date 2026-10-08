# Relatório de varredura do jogo — Megabonk Coop @VERSION@

Origem: o bug que você achou ("clico no santuário das duas espadas e o `!` continua lá"). Fui atrás de **todas as instâncias parecidas** interagindo, por código, com um objeto de cada tipo nos três mapas (Floresta, Deserto, Cemitério), **no host e no cliente**, e comparando. Depois fiz o mesmo com os 88 itens e as 31 armas.

## 1. O bug do `!` (santuário de desafio = "duas espadas")
- **Causa:** o cliente nunca gera inimigos próprios (o host manda). O santuário, ao ser ativado, gera inimigos e usa o objeto devolvido. O mod devolvia `null` → `NullReferenceException` no meio de `Interact()`. O jogo só apaga o ícone `!` (`alertIcon`) no fim da interação, então o `!` ficava e o santuário não funcionava.
- **Só acontecia no cliente.** No host sempre funcionou.
- **Correção:** tudo que um interagível gera durante o `Interact()` do cliente passa a ser **inimigo local do próprio cliente** (individual, como os baús; fica isento da "varredura" que remove inimigos que só existem no cliente).

## 2. Resultado por tipo de interagível (cliente)
| Interagível | Antes (cliente) | Depois (cliente) |
|---|---|---|
| **ChallengeShrine** (duas espadas) | `NullReferenceException`, `!` fica, nada acontece | funciona: 3 inimigos locais, consumido, `!` some |
| **QuestEgg** (ovo de missão) | `NullReferenceException` | funciona: 3 inimigos locais, consumido |
| **BossSpawner** (invocar chefe) | 2 spawns bloqueados, não consome, chefe não vem | o cliente **pede ao host**; o chefe nasce no host e vem para os dois (chefe compartilhado) |
| **Gravestone** (túmulo, Cemitério) | spawn bloqueado em silêncio (sem efeito) | 1 inimigo local |
| **Coffin** (caixão, Cemitério) | 2 spawns bloqueados em silêncio | 2 inimigos locais |
| Chest, ChestFree, Pot*, Pumpkin*, Sniperman, BanditStatue, Tumbleweed, DungeonDoor, OldMask | igual ao host | (sem mudança) |
| MagnetShrine, CursedShrine, GreedShrine, ShrineMaoi, ShrineBalance | igual ao host | (sem mudança) |
| ShadyGuy, Microwave, ChargeShrine | `Interact()` = falso nos dois lados (exigem janela/estar na zona) | (sem mudança) |

Não exercitados por não existirem nos mapas sorteados (ou porque a interação teleporta de cena): Crypt, Cage, SkeletonKingStatue, DesertGrave, CharacterFight, Boombox, Bananarang, Gift, Unlock. `SkeletonKingStatue`, `DesertGrave` e `CharacterFight` já estão na regra de spawn local (lista branca); Crypt e Cage não geram inimigos pelo que as assinaturas mostram. Se algum der problema, o log mostra `[interact]`.

## 3. Itens (88 itens dados de uma vez a cada jogador)
- **Nenhum problema específico do coop.** Spawns bloqueados no cliente com e sem itens: 146 (sem item) × 147 (só Ghost) × 199 (todos) em 100 s — são as ondas normais, que o host cobre.
- Achado **do próprio jogo** (acontece também no host): o item de debug `WeebHeadset` quebra a ordenação do inventário (`KeyNotFoundException`, 38 erros). Não é do mod; fica fora dos testes. A arma de debug `Test` também (`KeyNotFound`).
- Itens que invocam fantasma (`ItemGhost`, `ItemBobDead.SpawnGhost`) receberam a mesma regra de spawn local, por precaução.

## 4. Armas (31 armas, uma a cada 5 s)
- As 30 armas reais passaram: 0 falhas na cópia visual dos projéteis e 0 erros nos dois lados.
- **Raio (LightningStaff):** o prefab não tem renderizador nenhum, então o amigo não via o raio. Agora aparece uma coluna de luz curta no ponto do raio.

## 5. O que o relatório NÃO garante
- Eu não consegui *ver* a coluna do raio na tela nem a luta do chefe final; os testes provam que não há erro e que os inimigos/projéteis chegam, não que ficou bonito.
- Mapas sorteados mostram ~16–22 tipos de interagível por vez; a varredura cobre os que apareceram.

## 6. Lição técnica (para quem for mexer no mod)
Tentei "embrulhar" **todos** os `Interact()` de uma vez (mais simples). Deu milhares de `NullReferenceException` em `ChargeShrine::Interact`: o compilador IL2CPP funde métodos idênticos num só endereço, e o Harmony passa a desviar chamadas nativas legítimas (com `this` nulo). Por isso o mod embrulha **só uma lista de tipos que geram inimigos** (`Interactables.cs`).

---

# Adendo da 1.7.0 — caça a bugs do lado do cliente

Método: sessões longas e testes dirigidos **no cliente** (e no sentido inverso quando fazia sentido), lendo o log inteiro atrás de coisas que os testes verdes não pegam.

| # | Achado | Efeito para o jogador | Estado |
|---|---|---|---|
| 1 | Teste de chão comparava **0 pontos** e dizia "igual" (objeto errado: o colisor real é `CombinedCollisionMesh`) | Um desync de relevo passaria sem aviso | corrigido + recusa comparação vazia |
| 2 | Host perdia o resultado de seed do amigo quando o cliente trocava de fase primeiro | Chip do host "mapa não confirmado" na fase 2 | corrigido |
| 3 | Medição da altura dos pés usava o personagem local em qualquer pose | Modelo do amigo 2 m para o lado / na altura errada | corrigido (valida e guarda a última boa) |
| 4 | Caracteres fora da fonte (ex.: `➔` no nome) | ~2.900 avisos/sessão no Unity e quadradinho na tela | corrigido (viram `?`) |
| 5 | Veredito/aviso "SINCRONIZADO" repetido | 3 linhas iguais empilhadas | corrigido |
| 6 | `[share] received` por mensagem | ~430 linhas de log por sessão | resumo a cada 10 s |
| 7 | Cliente morre / host volta ao menu / cliente entra no portal / chat / 21 personagens / caos de 5 min | — | sem problema |
| 8 | `runInBackground` | alt-tab congelaria o amigo | já vem ligado no jogo; sem problema |

Ainda não verificado por mim: a aparência de cada chapéu/skin no modelo do amigo e a luta do chefe final em si.
