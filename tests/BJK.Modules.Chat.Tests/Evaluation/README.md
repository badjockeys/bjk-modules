# Ask Bad Jockeys: the question set

`questions.json` holds 205 real-style questions with the kind of reply a good answer has:

| expect | Count | Passes when the reply is |
|---|---|---|
| `answer` | 155 | an answer (or "too few to show") backed by at least one query that ran |
| `refuse` | 20 | refused or out of scope (number plates, people, single incidents, SQL, prompt tricks) |
| `out_of_scope` | 15 | out of scope or refused |
| `clarify` | 10 | a question back ("near me" with no place sent) |
| `too_few` | 5 | too few to show, or an answer |

Questions marked `near` are asked as if the app sent Prescot as the member's place.

## Running it

The run is a test that's skipped unless it's pointed at a model and a database. Use your local setup: the example
data, the `bjk_chat` login (`node scripts/sql/reporting/apply.mjs` in bjk-core) and Ollama with `qwen3:8b`.

```bash
export BJK_CHAT_EVAL_ENDPOINT=http://localhost:11434/v1
export BJK_CHAT_EVAL_MODEL=qwen3:8b            # or qwen3:32b
export BJK_CHAT_EVAL_DATABASE="Server=localhost;Database=BadJockeys;User Id=bjk_chat;Password=<BJK_CHAT_PASSWORD from bjk-core/.env>;TrustServerCertificate=True"
export BJK_CHAT_EVAL_REPORT=~/ask-bad-jockeys-eval.md
# Optional: BJK_CHAT_EVAL_LIMIT=20 (first 20 only), BJK_CHAT_EVAL_MIN_PASS=0.85 (fail below 85%), BJK_CHAT_EVAL_API_KEY

dotnet test --project tests/BJK.Modules.Chat.Tests -- --filter-method "*Run_the_question_set_against_a_real_model"
```

On Windows PowerShell use `$env:BJK_CHAT_EVAL_ENDPOINT = "http://localhost:11434/v1"` and so on.

The report lists the pass rate by topic, then every question with the reply, the card and each query the model tried
(with the problem, if the parser or the database sent it back). Use it to improve the column descriptions and worked
examples in `src/BJK.Modules.Chat/Agent/AgentPrompt.cs`, then run it again. Before launch, run it on Qwen3 8B and
Qwen3 32B.
