# 👻 Chomper

O gameplay atual está em `src/Chomper.Core/ChomperGame.cs` e usa a
implementação procedural em `src/Chomper.Core/Game/`. O jogador controla o
fantasma selecionado; os outros fantasmas vivos são aliados NPC.

O labirinto é gerado com seed determinística por fase, e o gameplay usa apenas
formas desenhadas por código com `SamplerState.PointClamp`. Não há dependência
de mapa Tiled para as fases.

O HUD usa `Fonts/Hud.spritefont`, configurado para carregar a fonte
`Array-Regular` de `src/Chomper.Core/Content/Fonts/Array-Regular.otf`.

Controles desktop: WASD/setas ou gamepad; `1`, `2`, `3` e `Tab` trocam o
fantasma. `Space` ou `F` solta uma fruta armadilha; gamepad usa `X`. No mobile,
swipe movimenta e toque nos indicadores superiores troca o fantasma.

Os fantasmas usam A*, Traveller Salesman heurístico e FSM, um por fantasma.
Pac-Man alterna o algoritmo após peletes e a política Q-learning online aprende
com a direção do fantasma controlado.

Pac-Man corre a 1,75x a velocidade normal dos fantasmas. Uma fruta armadilha
reduz sua velocidade para 0,5x durante alguns segundos e o torna capturável.
Cada fantasma possui três armadilhas. A fruta de poder duplica o Pac-Man e
reduz a velocidade dos fantasmas.

Os helpers amarelo e azul só deixam a jaula depois que Pac-Man consome 25% das
pebbles. Pac-Man possui três vidas; cada captura restaura as pebbles até o
último checkpoint e a vitória exige três capturas. Se todas as pebbles forem
consumidas, Pac-Man vence e ocorre game over.

## Build

```powershell
dotnet build
```

Se uma execução interrompida deixar o `.mgcontent` do Android bloqueado, feche
outros builds/IDE que estejam compilando e execute:

```powershell
dotnet build-server shutdown
dotnet clean
dotnet build
```
