# Megabonk Coop

Cooperativo online para **2 jogadores** no Megabonk (host + 1 amigo) por **IP direto**
(Radmin VPN, Hamachi, ZeroTier, rede local ou porta UDP aberta). Plugin BepInEx 6 (IL2CPP) para o **Megabonk 1.0.69**. Versao **@VERSION@**.

> Mod **nao oficial**, feito com ajuda de IA (Claude). Nada e enviado a servidores e as pontuacoes de runs coop **nao** vao para o ranking.

## O que sincroniza
| Sincronizado | Como |
|---|---|
| **Mapa** (relevo, baus, santuarios) nos 3 mapas | Seed de sessao compartilhada + os dois geradores aleatorios semeados igual; o teste so compara o colisor do terreno |
| **Inimigos, chefes, dano e mortes** | O host manda em tudo; o dano do amigo vale no host; vida x1.5 com 2 jogadores |
| **XP e ouro (soma)** | Tudo que um ganha o outro ganha inteiro; gastar e individual |
| **Reviver com paridade** | Quem morre e volta a run recebe de uma vez o XP/ouro que a dupla somou |
| **Projeteis, modelo, skin, chapeu, vida, aura/escudo** do amigo | Enviados e desenhados no modelo dele |
| **Troca de fase (portal)** | Um entra, o outro vai junto |
| **Santuario de desafio, ovo de missao, tumulos, caixoes** | Rodam no PC de quem ativou (inimigos so dele); o **chefe** e invocado pelo host e e dos dois |
| **Santuario de desafio, ovo de missao, tumulos, caixoes** | Rodam no PC de quem ativou (inimigos so dele); o **chefe** e invocado pelo host e e dos dois |
| **Sem pausa** | O jogo nunca pausa; invencivel (max 30 s) enquanto uma janela esta aberta |
| **Chat (T), marcador de ponto (G), teleporte (H), avisos do que o amigo pega, placar de dano/abates, nome do amigo** | Mensagens confiaveis |

## Instalar (os dois jogadores)
Extraia o zip, **INSTALAR.bat** (instalacao limpa com backup). Os dois precisam estar na **mesma versao**. Para so atualizar a DLL use o pacote **ATUALIZAR**. Voltar ao original: **DESINSTALAR.bat**.

## Jogar
1. **Host:** menu COOP -> HOSPEDAR -> copie o seu IP e mande ao amigo.
2. **Amigo:** cole o IP -> ENTRAR NO HOST. Uma lista de verificacao mostra cada etapa e o que fazer se falhar.
3. **Host** clica em JOGAR; o amigo **entra sozinho** em 3 s.
4. Chip verde "COOP: SINCRONIZADO" = mesmo mapa. Porta padrao **UDP 27055**.

Teclas: `T` chat · `G` marcar ponto · `H` teleportar ate o amigo · `F7` menu · `F10` desconectar.

## Opcoes (menu COOP -> OPCOES, ou `BepInEx\config\luis.megabonkcoop.cfg`)
`ShareXpGold`, `AutoJoin`, `NoPause`, `SplitSpawns`, `SpawnTogether`, `BlockLeaderboardUpload`, `HpScale`, `NativeUi`, `JoinIP`, `Port`, `Verbose`.

## Problemas
Menu COOP -> OPCOES -> **SALVAR O LOG NA AREA DE TRABALHO** e mande o `coop-log-*.log` **dos dois** (ou o `BepInEx\LogOutput.log`). O `RELATORIO.md` descreve a varredura do jogo e os bugs corrigidos. Prefixos: `[sync]`, `[terraincheck]`, `[objcheck]`, `[version]`, `[share]`, `[marker]`, `[autojoin]`, `[nui]`.

## Creditos e licencas
BepInEx 6 (LGPL-2.1), Harmony e Il2CppInterop (incluidos no pacote completo). Estudo de abordagem (sem copiar codigo) de MegabonkTogether, BonkCoop, multibonk e MegaBonk.Multiplayer. Nenhum arquivo do jogo e distribuido.
