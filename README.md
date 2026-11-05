# ChessSlackBetter

Play chess against other people in Slack threads. A C# / .NET 10 rebuild of
[chessbot](https://www.chris-saylor.com/chessbot/).

- `@ChessBetterBot challenge @someone` posts a challenge card; each game gets its own thread
- Board images after every move (from the mover's point of view), with last move and check highlighted
- Moves in long (`e2e4`, `e1g1`, `c7c8q`) or standard notation (`e4`, `Nf3`, `O-O`, `exd8=Q`)
- Type moves in the thread without mentioning the bot, if you like
- Turn notifications, resign / draw offers / abort (buttons or commands)
- Full rules: castling, en passant, promotion, checkmate, stalemate, threefold repetition, fifty-move rule, insufficient material
- Finished games are imported to lichess for computer analysis; PGN export any time
- `@ChessBetterBot stats`, `@ChessBetterBot leaderboard`, `@ChessBetterBot games`
- [Elo ratings](#ratings) for each mode, with the change shown when a game ends

## Commands

| Where | Command | |
|---|---|---|
| Anywhere | `@ChessBetterBot challenge @user [white\|black]` | Challenge someone (random colours by default) |
| Game thread | `@ChessBetterBot accept` / `decline` | Answer a challenge (or use the buttons). The challenger can `decline` to withdraw |
| Game thread | `@ChessBetterBot e2e4`, or just `e4` | Make a move |
| Game thread | `@ChessBetterBot resign` | Resign |
| Game thread | `@ChessBetterBot draw` / `decline draw` | Offer, accept or decline a draw |
| Game thread | `@ChessBetterBot abort` | Cancel before both players have moved (not counted in stats) |
| Game thread | `@ChessBetterBot board` / `pgn` | Re-post the board / export the game |
| Anywhere | `@ChessBetterBot chess960 @user [white\|black]` | Challenge someone to [Chess960](#chess960) |
| Anywhere | `@ChessBetterBot threeplayerchess @user @user` | Challenge two people to [Three-Player Chess](#three-player-chess) |
| Anywhere | `@ChessBetterBot games` | Your open games, with links |
| Anywhere | `@ChessBetterBot stats [@user]` / `leaderboard` | Results and [Elo ratings](#ratings) |
| Anywhere | `@ChessBetterBot theme [brown\|blue\|green\|purple\|grey\|pink\|bolt]` | Show or set your board colours, used for the board on your turn |
| Anywhere | `@ChessBetterBot pieces [cburnett\|chessnut\|fantasy\|rhosgfx\|bolt]` | Show or set your piece set, used for the board on your turn |
| Anywhere | `@ChessBetterBot help` | Help |

## Ratings

Two-player games are rated with [Elo](https://en.wikipedia.org/wiki/Elo_rating_system). Everyone starts at 1200,
and after each game both players move by 32 × (score − expected score). The score is 1 for a win, ½ for a draw and
0 for a loss. Two equal players swap 16 points, and beating someone 400 points above you is worth about 29.

- Standard, ChessDiceRoll and Chess960 each have their own rating, and `leaderboard` is ranked by rating.
- The game-over message shows both players' new ratings and how much they moved, and `stats` shows your rating
  for each mode.
- Aborted games don't count. Ratings aren't stored. They're replayed from every finished game in the order the
  games ended, so games played before ratings existed count too.
- Three-player games keep their placement points.

## ChessDiceRoll

A variant: normal chess, but on your turn you may gamble on a d20 instead of moving.

```
@ChessBetterBot diceroll @friend
...
@ChessBetterBot roll turn my e4 pawn into a bishop, if I fail I lose the pawn
  → Success: e4 pawn becomes a bishop. Failure: e4 pawn removed.
    Roll 14+ on a d20 (35% chance).  [Roll] [Cancel] [Veto]
```

- Every gamble needs a reward **and** a penalty. Claude reads the request and turns it into board changes;
  code checks they're legal and sets the odds, and the die is rolled with a cryptographic RNG.
- **Fair odds:** with piece values pawn 1, knight/bishop 3, rook 5, queen 9, a gamble that gains `G` on success
  and loses `L` on failure should succeed with probability `L / (G + L)`. Claude can shift the values by up to
  ±3 pawns for position (e.g. a new piece that forks the queen). The target is the d20 roll that matches.
- Only the requester can **Roll** (or say `yes`) or **Cancel** (`no`). Rolling uses your turn.
- Each player gets **5 rolls** per game. Cancelled or vetoed gambles don't count, and don't use your turn.
- The opponent gets **1 veto** per game.
- Dice can't touch kings, deliver checkmate (or set one up within two moves), stalemate, or leave your own king in check.
- Gambles can be pointless (they just get easy odds) or random ("a random one of their pieces becomes a pawn");
  random outcomes are priced on their average and picked when the die is rolled.
- Dice games have their own stats: `@ChessBetterBot leaderboard dice`, and `stats` shows gambles won.

Dice mode needs an [Anthropic API key](https://console.anthropic.com/) (`Anthropic:ApiKey`); everything else
works without one.

## Chess960

`@ChessBetterBot chess960 @user` (also `960`, `fischerrandom`, or `challenge @user 960`) starts a game of
[Fischer Random](https://en.wikipedia.org/wiki/Fischer_random_chess): normal rules, but the back-rank pieces start
in one of 960 shuffled orders, mirrored for both sides. It takes opening preparation out of the game.

- The challenge card shows the position number and back rank. Every shuffle except #518 (the normal start) can come up.
- Bishops are always on opposite colours and the king always starts between the rooks.
- Castle with `O-O` / `O-O-O`. The king ends on g or c and its rook on f or d, as in normal chess, however far they
  start from there. Every square either piece crosses must be empty, and the king can't start in, pass through, or
  land in check. Moves are stored king-onto-rook (`b1h1`); typing the king's destination (`b1g1`) also works,
  and if that could also be a plain king step the bot asks you to say `O-O` or `Kg1`.
- Finished games are imported to lichess as Chess960 (the PGN carries the starting FEN).
- `@ChessBetterBot leaderboard 960` has its own table; `stats` shows Chess960 results separately.

## Three-Player Chess

`@ChessBetterBot threeplayerchess @user @user` challenges two people to a three-way game on a triangular board
(the 96-square "Yalta" layout, drawn as a triangle with its corners cut off). Seats are drawn at random and
everyone has to accept.

- Each player has the usual 8×4 half: R N B Q K B N R with pawns in front. Squares are named with files a–l and
  ranks 1–12: White has a–h on ranks 1–4, Black l–i and d–a on ranks 8–5, Red h–e and i–l on ranks 12–9.
- Turns go White → Black → Red. Files and diagonals carry on through the middle into the other halves, so the
  a–d files join White to Black, e–h join White to Red, and i–l join Black to Red. A diagonal through the very
  centre touches both other halves, so it can go either way (a bishop on d4 can head for e9 or i5).
- Pawns head for the middle, then on to the back rank of whichever half they enter, and promote there to any
  piece. The distance is the same eight ranks as in normal chess. There is no en passant.
- Kings are never captured. If you're in check on your turn with no way out, you're checkmated: you're out and
  your pieces leave the board. The last player standing wins.
- A stalemated player passes their turn; once only two are left, stalemate is a draw. You can resign at any time,
  and a draw needs everyone still playing to agree.
- `@ChessBetterBot leaderboard 3p` ranks players by finishing place: 1 point for 1st, ½ for 2nd. Players who share
  a place split the points for the places they cover.

### With dice

`@ChessBetterBot threeplayerdice @user @user` plays the same game with [ChessDiceRoll](#chessdiceroll) gambles.

- Each player gets **5 rolls** and **1 veto**, and either opponent can veto a gamble.
- **Odds:** a gamble's value to you is your own change in material minus the *average* change for your opponents
  still in the game. Costing one opponent a queen helps the third player too, so it's worth half a queen to you;
  gaining a queen yourself is worth a whole one. With one opponent left, this is the same as two-player dice.
- Dice can't touch kings, checkmate anyone, leave an opponent with no legal move, or leave your own king in check.
  (The two-player rule against setting up a mate in two isn't checked here.)
- `@ChessBetterBot leaderboard 3pdice` has its own table, with gambles won.

## Project layout

```
src/ChessSlackBetter.Chess   Rules engine: move generation, SAN/LAN, FEN, PGN, game end detection
src/ChessSlackBetter.Bot     ASP.NET Core app: Slack events + interactivity, SQLite storage, board PNGs,
                             ChessDiceRoll (Dice/: Claude referee + fair-odds rules)
tests/                       xUnit tests (perft-verified move generation, bot flows with a fake Slack)
```

## Running it

You need a public HTTPS URL that Slack can reach — both for the event/interaction webhooks and so Slack
can fetch the board images. For local play, [ngrok](https://ngrok.com/) works: `ngrok http 5216`.

1. **Create the Slack app.** At https://api.slack.com/apps choose *Create New App → From an app manifest*,
   paste [`slack-app-manifest.yml`](slack-app-manifest.yml) and replace `YOUR-PUBLIC-URL`. Install it to the
   workspace.
2. **Configure secrets.** From the app settings, copy the *Bot User OAuth Token* (`xoxb-…`) and the
   *Signing Secret*:

   ```sh
   cd src/ChessSlackBetter.Bot
   dotnet user-secrets init
   dotnet user-secrets set Slack:BotToken xoxb-...
   dotnet user-secrets set Slack:SigningSecret ...
   dotnet user-secrets set ChessBot:PublicBaseUrl https://your-subdomain.ngrok-free.app
   dotnet user-secrets set Anthropic:ApiKey sk-ant-...   # only needed for ChessDiceRoll
   ```

   (Environment variables work too: `Slack__BotToken`, `Slack__SigningSecret`, `ChessBot__PublicBaseUrl`.)
3. **Run.** `dotnet run --project src/ChessSlackBetter.Bot` (listens on http://localhost:5216).
   Slack will verify the event URL once the app is reachable — re-save the URLs in the app config if needed.
4. **Play.** Invite the bot to a channel (`/invite @ChessBetterBot`) and `@ChessBetterBot challenge @friend`.

If `PublicBaseUrl` is empty the bot still works but draws boards as text.

### Docker

```sh
docker build -t chessbot .
docker run -p 8080:8080 -v chessbot-data:/data \
  -e Slack__BotToken=xoxb-... -e Slack__SigningSecret=... \
  -e ChessBot__PublicBaseUrl=https://chess.example.com chessbot
```

### Settings

| Setting | Default | |
|---|---|---|
| `Slack:BotToken` | | Bot User OAuth Token |
| `Slack:SigningSecret` | | Used to verify requests come from Slack |
| `ChessBot:PublicBaseUrl` | | Public base URL for board images |
| `ChessBot:DatabasePath` | `chessbot.db` | SQLite file |
| `ChessBot:AllowMovesWithoutMention` | `true` | Accept bare moves like `e4` from players in a game thread |
| `ChessBot:ImportFinishedGamesToLichess` | `true` | Import finished games to lichess.org for an analysis link |
| `Anthropic:ApiKey` | | Claude API key for the ChessDiceRoll referee |
| `Anthropic:Model` | `claude-opus-5-5` | Model used to read dice gambles |

## Development

```sh
dotnet test
```

## Credits

Piece images are from the sets distributed by [lichess.org](https://github.com/lichess-org/lila/blob/master/COPYING.md),
in `src/ChessSlackBetter.Bot/Rendering/Pieces/`:

| Set | Author | Licence |
|---|---|---|
| cburnett | Colin M.L. Burnett | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) |
| chessnut | Alexis Luengas | [Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0) |
| fantasy | Maurizio Monge | [MIT](https://opensource.org/license/mit) |
| rhosgfx | RhosGFX | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |

The `bolt` set (fasteners, in Bolt Depot colours) was drawn for this project.
