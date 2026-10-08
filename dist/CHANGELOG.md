# Megabonk Coop — changelog

> Todos os jogadores precisam estar na **mesma versão** (a partir da 0.8.1 o mod avisa quando não estão).
> Marcado com **(não testado)** = foi escrito/compilado mas ainda não foi jogado por vocês.

## 1.7.0  — caça a bugs do lado do cliente (testado: suíte completa + varreduras + sessões com o jogo aberto em janela)
Sem versão A/B: nada experimental, só correções.
- **O teste de "mesmo chão" estava vazio** (0 pontos comparados, mas dizia "chão igual"). Ele olhava o objeto errado: o colisor real do terreno se chama `CombinedCollisionMesh` (camada 8). Agora compara de verdade (162 pontos) e, se não conseguir comparar quase nada, avisa "inconclusivo" em vez de dizer que está tudo certo. Revalidado nos 3 mapas: 162/162 pontos iguais.
- **Host não confirmava a 2ª fase quando o CLIENTE entrava no portal primeiro:** o resultado que o amigo já tinha mandado era apagado ao começar a geração. Resultado: o chip do host ficava em "mapa não confirmado" na fase 2. Corrigido (testado nas duas direções).
- **Modelo do amigo deslocado 2 m para o lado:** a altura dos pés era medida no SEU personagem, e quando ele estava em outra pose (corpo tombado/deslizando) o valor saía de lado. Agora a medida é validada e usa a última boa.
- **Nome Steam/mensagens com símbolos que a fonte do jogo não tem** (ex.: seta `➔`): geravam ~2.900 avisos no log do Unity por sessão e um quadradinho na tela. Agora viram `?`.
- **"SINCRONIZADO!" aparecia 3 vezes empilhado:** agora uma vez por fase, e avisos idênticos não empilham mais.
- **Log menor:** os ganhos pequenos de XP/ouro viram um resumo a cada 10 s (eram ~430 linhas por sessão).
- **Testado e sem problema:** o cliente morrer (4/4), o cliente entrar no portal, o host voltar ao menu (o cliente assume os inimigos em ~7 s), chat nos dois sentidos (e corte em 120 caracteres), os 21 personagens como amigo, caos de 5 min (todos os itens + todas as armas + chat + marcadores), `runInBackground` (alt-tab não congela o jogo).
- Novos testes automáticos: `portalrev` (portal iniciado pelo cliente) e verificação de que o teste de chão comparou pelo menos 100 pontos.

## 1.6.1  — varredura do jogo (testado: suíte completa + varreduras por tipo de objeto, item e arma)
- **Corrigido o `!` que ficava no santuário das duas espadas (santuário de desafio):** no cliente a interação dava erro no meio porque o cliente não gera inimigos próprios. Agora esses inimigos são do próprio jogador (individual).
- **Mesma correção para** o ovo de missão, túmulos e caixões do cemitério (antes o spawn era bloqueado em silêncio).
- **Invocar o chefe pelo cliente:** o pedido vai ao host, o chefe nasce lá e vem para os dois (antes nada acontecia).
- **Raio (LightningStaff) agora aparece para o amigo** (o prefab não tinha visual; desenhamos uma coluna de luz curta).
- **Botão "SALVAR O LOG NA AREA DE TRABALHO"** em OPCOES (cria `coop-log-...log` pronto para enviar).
- **Relatório da varredura** (`RELATORIO.md`): 3 mapas × todos os tipos de interagível, 88 itens e 31 armas, host × cliente. Conclusão: itens e armas não têm problema específico do coop; os interagíveis que geram inimigos tinham.
- Novos testes automáticos: `interact` (varre os interagíveis do Deserto e o log salvo), e ferramentas de varredura (`explore`).

## 1.5.0  — versão final desta fase (testado: suíte completa + soak de 40 min + testes com jogo aberto em janela)
Resumo de tudo que mudou desde a 0.8.2 (detalhes nas versões abaixo):
- **XP e ouro 100 % somados** entre os dois; **reviver com paridade** (quem volta recebe o progresso da dupla).
- **Interface nativa do jogo** (menu de entrada com lista de verificação, IPs recentes, entrada automática, opções, chat, avisos, marcador do amigo, chip de status).
- **Marcar ponto (G), teleporte até o amigo (H), nome do amigo, placar de dano/abates.**
- **Vida dos inimigos x1,5 com 2 jogadores**, host morto/saiu não congela mais o cliente, amigo que some devolve o jogo ao normal.
- **Corrigido o "mapas diferentes" intermitente** (o teste de chão contava inimigos/gemas) e o desync raro do Deserto.
- **Mais robusto:** reconexão testada, rede com 140 ms de ping + jitter + 5 % de perda testada, patches aplicados um a um, aviso de versão do jogo.
- Novo nesta versão: **placar de dano e abates** (chip na parte de baixo da tela, valores do host), simulação de latência nos testes (`Latency`/`Jitter`).

## 0.9.3  — (testado em máquina local: suíte completa)
- **Teleporte até o amigo (tecla H):** quando vocês se separam, a tecla H leva você para perto dele (recarga de 45 s; só funciona a mais de 12 m de distância). Testado com os dois a 150 m.
- **Nome Steam do amigo** nos marcadores, no chip e no menu (antes aparecia "AMIGO").
- **Mais resistente a atualização do jogo:** os patches são aplicados um a um; se uma atualização remover algo que o mod usa, só aquele recurso se perde (e vai para o log) em vez de o mod inteiro parar. O menu avisa se a versão do jogo for diferente da 1.0.69.
- Testes: o do chapéu aceita personagens sem osso "Head" (ex.: Dicehead).

## 0.9.2  — (testado em máquina local: suíte completa)
- **Reviver com paridade:** quem morre volta ao menu e (com a entrada automática) retorna à run do host recebendo de uma vez todo o XP e ouro que a dupla já somou. Volta no mesmo nível de antes (testado: +51 xp, +60 ouro, nível 0 → 3).
- **Vida dos inimigos x1,5 com os dois jogadores** (só o host; `HpScale`, muda em OPCOES). Dois jogadores matam muito mais rápido.
- **Tela OPCOES** no menu COOP: liga/desliga XP+ouro somados, entrada automática, sem pausa, divisão de spawns, nascer juntos, ranking Steam, vida dos inimigos e interface nativa.
- **Host morreu / voltou ao menu e o amigo continua jogando:** depois de 8 s os inimigos voltam a ser do amigo (antes ficavam congelados).
- **Estágio final (chefe final):** testado automaticamente que o amigo vai junto e os dois ficam na cena do chefe sem erros.
- README e LEIAME novos (gerados com a versão certa a cada pacote).

## 0.9.1  — (testado em máquina local: suíte completa + testes com o jogo aberto em janela)
- **Interface nativa do jogo** (uGUI + TextMeshPro, com a fonte e o estilo do próprio Megabonk) no lugar do IMGUI: menu de entrada, avisos, chat (T), chip de status e marcador do amigo. Se não conseguir criar, volta sozinho para o IMGUI (`NativeUi = false` desliga).
- **Menu de entrada novo:** HOSPEDAR / ENTRAR com campo de IP, botão Colar, **últimos IPs usados**, todos os seus IPs (Radmin/LAN), lista de verificação ao vivo (conectado, mesma versão, sessão, host pronto, mapas sincronizados), aviso do que fazer quando o host não responde e botão REFAZER.
- **Entrar automático:** quando o host inicia a run, o amigo entra sozinho em 3 s (sem clicar JOGAR). `AutoJoin = false` desliga.
- **Marcar ponto no mapa (tecla G):** aparece pro amigo por 10 s, com distância e seta na borda da tela se estiver fora da visão.
- **Corrigido o "mapas diferentes" intermitente (com o jogo aberto):** o teste de chão contava inimigos, gemas e projéteis em cima do terreno como se fossem o chão, e eles ficam em lugares diferentes em cada PC. Agora só o colisor do terreno conta.
- Novos testes automáticos: `autojoin`, `marker`.

## 0.9.0  — (testado em máquina local: suíte completa)
- **XP e ouro 100 % compartilhados (soma, não divisão):** tudo que um ganha, o outro ganha inteiro, em tempo real (reliable, agrupado a cada 0,25 s). Gastar ouro (baús, lojas) continua individual. Desligue com `ShareXpGold = false`.
- **Falso "chão diferente":** em PCs mais lentos o colisor do terreno termina de montar tarde e o teste de chão caía no "piso do mundo". Agora o teste espera o chão ficar estável (duas amostras iguais) antes de comparar.
- Nova checagem automática de reconexão e de amigo que some (cenários de teste `rejoin`, `killpeer`, `killhost`, `share`).

## 0.8.3  — (testado em máquina local: suíte de 11 cenários + estabilidade)
- **Mapa do Deserto às vezes saía diferente** (o chão mudava em ~1 de cada 2 partidas): o gerador pausa entre frames e outras coisas do jogo mexiam no sorteio. Agora os dois PCs ressorteiam igual a cada passo da geração.
- **Amigo sumiu / jogo fechou:** depois de 12 s sem sinal o mod volta ao normal (o jogo pode pausar, sem invencibilidade de janela, os inimigos do cliente voltam a nascer). Antes o host ficava sem poder pausar e o cliente num mapa vazio.
- **Travado em "espere o host":** na 3ª tentativa de clicar JOGAR (em 15 s) a run é liberada mesmo sem a confirmação (ou na hora, se o amigo sumiu).
- **Reconectar:** testado o amigo fechando e abrindo o jogo de novo no meio da run: ressincroniza.
- Desconectar agora limpa todo o estado (run do host, invencibilidade, versão).
- Menos avisos no log da Unity (velocidade em corpos cinemáticos).

## 0.8.2  — (testado em máquina local: suíte de 8 cenários + estabilidade)
- **Aura e escudo do amigo:** corrigida a escala (aparecia uma bolinha azul por cima do personagem; a aura do jogo é 5,5× maior).
- **Invencibilidade com janela aberta tem teto de 30 s:** deixar a tela de baú aberta não protege mais "para sempre".
- **Aviso claro quando o host começa uma run nova** e você ainda está na antiga ("volte ao menu e clique JOGAR"), o que causava o desync "tu ainda tava na partida quando iniciei".
- **Cronômetro da fase sincronizado** com o do host (depois de entrar de novo na run). Só é corrigido **depois** que a geração do mapa terminou, porque mexer nele durante a geração mudava os objetos e o chão.
- Ferramenta de teste: `tools/ci.ps1` roda a suíte inteira (mapas, portal, perda de pacotes, chapéu, spawns, versão).

## 0.8.1  — (não testado)
- **Verificação de versão:** host e amigo com versões diferentes → aviso vermelho nos dois e ninguém inicia a run.
- **Chat mostra o que o amigo pega:** `* Luis pegou: arma FireStaff`, `item …`, `tomo …`, `buff Rage` (máx. 6 avisos / 5 s; também vai para o log).
- **Agachar** do amigo aparece no modelo dele.
- **Aura e escudo** do amigo aparecem no modelo dele — *experimental*.
- O host passa a ignorar pacotes que não venham do amigo.

## 0.8.0  — (testado só em máquina local, ainda não jogado por vocês)
**Jogabilidade**
- **Inimigos para os dois jogadores:** metade dos inimigos de cada onda agora nasce ao redor do **amigo** (antes tudo nascia em volta do host e o outro quase não via inimigo). Teste com os jogadores a 260 m de distância: 0 inimigos perto do amigo antes → 9 a 36 depois. Opção `SplitSpawns` no config (só o host precisa).
- **Troca de estágio:** quando um passa para o próximo estágio o outro vai junto, mesmo sem o objeto do portal na tela. Testado: o estágio 2 sincroniza.

**Rede**
- **Mensagens confiáveis:** dano, mortes, troca de estágio, chat e checagens são reenviados até o outro confirmar e duplicatas são descartadas (antes ~1 % se perdia). Provado com 30 % de perda de pacotes simulada.
- As checagens de chão e objetos passaram a ser **etiquetadas por run**, então um resultado velho nunca é comparado com um novo (acabou o falso "mapas diferentes").

**Correções**
- Corrigido um bug em que o aviso de "entrei no portal" disparava **ao iniciar** a run, fazendo o outro lado gerar o mapa duas ou três vezes.
- Chapéu do amigo: agora encaixa no osso `Head` do modelo (ainda não conferido visualmente).

**Verificado em teste local (host + cliente):** Floresta, Deserto e Cemitério sincronizam (seed, chão e objetos). O Inferno não existe nos dados deste jogo.

## 0.7.1
- **O host ainda pausava** ao abrir baú/level up: o jogo pausa por um campo interno (`MyTime.paused`). Agora um vigia despausa a cada frame e você fica **invencível sempre que não puder agir** (qualquer janela).
- **Chapéu e skin** do amigo enviados e aplicados (chapéu não conferido).
- O jogo não envia mais a pontuação de uma run **coop** para o ranking da Steam.
- Corrigido o falso "SEEDS DIFERENTES" por resultado de run antiga.
- Ao **entrar de novo numa run em andamento**, a checagem de objetos é pulada.

## 0.7.0
- **Projéteis do amigo visíveis** (antes copiava o contêiner do ataque, que é invisível; agora copia o projétil real).
- **O jogo não pausa mais no coop**; invencibilidade enquanto a tela de level up/baú está aberta (máx. 30 s) + 1,5 s depois.
- **Morte de inimigos e chefes** refeita (dano = vida+1, evento "chefe derrotado" disparado à mão, morte que chega antes do spawn não cria fantasma, inimigo com 0 de vida é removido).
- **Varredura:** o cliente remove inimigos que só existem nele.
- **Vocês nascem juntos** (o cliente nasce ao lado do host).
- **Entrar de novo na mesma run** depois de morrer.
- Chat com quebra de linha; log bem menor.

## 0.6.0 — a correção do desync do mapa
- **Seed compartilhada:** o host sorteia uma seed de sessão ao hospedar; no início de **cada** mapa os dois PCs semeiam os **dois** geradores aleatórios do jogo com o mesmo valor. Resultado: chão e objetos idênticos.
- O host **não inicia** a run antes do amigo conectar e confirmar; o cliente só entra depois do aviso "Host iniciou a run!".
- Mapa, estágio e nível do host são impostos no cliente.
