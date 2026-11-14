# NAS Chatbot

An offline help chatbot for **NAS POS Pro**. Staff type a question ("Where can I create user roles?") and get the
answer from a question/answer list that you manage. No internet, no AI service and no database are needed. It
runs on the .NET 9 SDK that NAS POS already uses.

## Start it

Double-click `start-chatbot.cmd`, or:

```powershell
cd chatbot/src/NasChatbot
dotnet run --no-launch-profile
```

- Chat: **http://localhost:5090**
- Manage questions: **http://localhost:5090/admin.html** (or the **Manage Q&A** button)

The first time you open the admin page **on this computer** it asks you to set the admin password (at least 8
characters with letters and digits). No default password ships.

## What it starts with

About 90 answers taken from the NAS POS Pro user guide, covering setup, settings, users & roles, masters,
products & stock, purchase, sales, accounts, GST & reports, backup & year end, shortcuts and common errors.

## Managing answers (admin page)

| Tab | Use it to |
|---|---|
| **Questions** | Add, edit or delete a question. Put other ways people ask it in **Other ways people ask it**, one per line. More wordings give better matching. **Keywords** add extra search words. Answers support `**bold**`, `- ` bullets and `1. ` steps. |
| **Unanswered** | See questions the bot couldn't answer and answers people marked 👎. **Add answer** opens a new question pre-filled with what was asked. |
| **Test the bot** | Type a question to see which entries match and how confident the bot is. |
| **Import / export & password** | Export all questions to `.json` (as a backup or to copy to another PC), import them back, and change the admin password. |

Changes take effect immediately; no restart is needed.

## How matching works

`src/NasChatbot/Knowledge`:

- `TextNormalizer.cs`: lower-cases the text and joins terms such as "e-mail", "back up" and "GSTR-1". It drops filler
  words ("how", "do", "I") and strips word endings (created/creating become create). It also folds synonyms
  (invoice→bill, add/new/make→create, vendor→supplier and so on). Edit `SynonymGroups` to add your own.
- `AnswerEngine.cs`: scores every entry. A rare word counts more than a common one. Most of the score is how much of
  the question the entry covers; the rest is how close it is to one of the entry's wordings. Typos of 5+ letter words
  are corrected against known words.
  - **55% or higher**: the bot answers.
  - **40–55%**: the bot answers with "I think you are asking…".
  - **Below 40%**: the bot says it doesn't know and logs the question under **Unanswered**.

## Data

Everything lives in `src/NasChatbot/App_Data/` (change it with `Chatbot:DataDir` in `appsettings.json`):

| File | Contents |
|---|---|
| `knowledge.json` | the questions and answers (on first start this is copied from `seed/knowledge.seed.json`) |
| `unanswered.json` | questions the bot couldn't answer |
| `admin.json` | the admin password as a salted PBKDF2 hash |

Back up `knowledge.json`, or use **Export questions**. Delete `knowledge.json` to start again from the seed.

## Network use

By default it listens only on this computer (`http://localhost:5090`). To let other counters use it, change
`Urls` in `appsettings.json` to `http://0.0.0.0:5090` and allow the port in Windows Firewall (private networks). The
admin password can only be set from the computer running the bot. After that, anyone with the password can manage
answers from any counter.

## Tests

```powershell
cd chatbot
dotnet test
```

The tests check that real questions, including ones with typos, reach the right answer and that unrelated
questions are not answered. They also check that every stored wording finds its own entry, plus admin sign-in,
add/delete, import and persistence.
