# Deploying BunonBrain on Render (free plan)

The Blueprint is [render.yaml](render.yaml). This guide covers what it does not:
the external services, the secrets, the PDF data and the computer-vision PC.
Hosting is free: Render free web services, Supabase free, Qdrant Cloud free and the Gemini
API free tier (embeddings). Answers and QC use OpenAI, which is paid per use.

## 1. Where each part runs

| Part | Runs on | Plan |
|---|---|---|
| Frontend (Next.js) | Render web service `bunonbrain-web` | free |
| API (.NET 10, Docker) | Render web service `bunonbrain-api` | free |
| PDF RAG (FastAPI, Docker) | Render web service `bunonbrain-rag` (every route except `/health` needs `PYRAG_API_KEY`, which only the API has) | free |
| Database | Supabase (as today) | free |
| Vector store | Qdrant Cloud, 1 GB cluster | free |
| Answers (Ask) + QC vision | OpenAI API (`gpt-6-luna`, QC `gpt-4o`) | paid per use |
| Embeddings (PDF search) | Gemini API (`gemini-embedding-001`) | free tier |
| Computer vision | a PC inside the factory (it must reach the RTSP cameras on the factory LAN) | your PC |

```
Browser ──https──> bunonbrain-web ──https──> bunonbrain-api ──> Supabase
                                               │   ▲
                       https (X-Rag-Api-Key)   │   │ https (X-Service-Key)
                                               ▼   │
                                       bunonbrain-rag ──> Qdrant Cloud, Gemini, OpenAI
                                                   │
                               Factory PC: computer-vision ──> cameras (RTSP)
```

All Render services are in **Singapore** (closest to Supabase in Mumbai).

### What the free plan means

| Limit | Effect on BunonBrain |
|---|---|
| A service sleeps after 15 minutes without requests and needs ~1 minute to wake | First visit after a quiet period is slow. The first request can fail with a timeout (the frontend's `/api` proxy gives up after 30 s) — reload and it works. |
| No disk; the filesystem is wiped on every sleep, restart and deploy | **Uploaded files disappear**: QC images, merchandising/factory documents, worker photos, camera videos. Their database rows stay, so those pages show broken images/files. Everything stored in the database (users, planning, production, attendance, monitoring, analytics, Ask) works normally. |
| Background workers only run while the API is awake | QC analysis, document extraction and reminders pause while it sleeps and continue when it wakes. |
| 750 free instance hours per month for the whole workspace | One service awake all month uses 744 h. If the total runs out, Render suspends the free services until the next month. |

**To stay within 750 hours:** don't keep services awake on purpose. No uptime pinger; close
browser tabs of the app when done (an open tab keeps a live connection); run the CV service and
IoT devices only while you demo, because they call the API every few seconds and keep it awake.

**Other free tiers:** Supabase pauses a free project after about a week without activity, and
Qdrant Cloud can suspend an unused free cluster. Resume them from their dashboards if Ask or
login suddenly fails after a long break.

## 2. Before you start

1. **Supabase.** Dashboard → Connect → *Session pooler* (port 5432, never 6543). Add
   `;Maximum Pool Size=10`, because the pooler allows only 15 clients and your local API uses
   some of them too:
   ```
   Host=aws-0-ap-south-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<ref>;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=10
   ```
   A separate Supabase project for the hosted app is cleaner (no test data, own connection
   limit). The API creates the `vector` extension, migrates and seeds the admin on first start.
2. **Qdrant Cloud.** cloud.qdrant.io → *Create cluster* → Free, AWS, an Asia region → copy the
   cluster URL (`https://….cloud.qdrant.io:6333`) and create an API key.
3. **Keys.** OpenAI key (platform.openai.com/api-keys) for the API and the RAG answers; Gemini
   key (aistudio.google.com/app/apikey) for the RAG embeddings.
4. **Code on GitHub.** Render deploys from one branch: `frontend-integration-and-admin-roles`
   (or merge into `main`).

## 3. Create the Blueprint

Render dashboard → **New → Blueprint** → connect GitHub → repo `FactoryBrain` → branch.
Render reads `render.yaml`, shows the three free services and asks for these values:

| Service | Key | Value |
|---|---|---|
| api | `BUNONBRAIN_DB` | connection string from step 2.1 |
| api | `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` | first Super Admin (strong password) |
| api | `AI_SETTINGS_ENCRYPTION_KEY` | same database as local: copy from local `.env`. New database: 32 random bytes in base64 (`openssl rand -base64 32`) |
| api | `CORS_ORIGINS`, `PUBLIC_APP_URL` | `https://bunonbrain-web.onrender.com` |
| api | `LLM_API_KEY` | OpenAI key |
| rag | `PYRAG_OPENAI_API_KEY` | OpenAI key |
| rag | `GOOGLE_API_KEY` | Gemini key (embeddings only) |
| rag | `PYRAG_QDRANT_URL`, `PYRAG_QDRANT_API_KEY` | from Qdrant Cloud |

`JWT_SIGNING_KEY`, `CV_SERVICE_KEY` and `PYRAG_API_KEY` are generated by Render; the API gets
the RAG key automatically. Click **Apply**. The first build takes 10–15 minutes; follow each
service's **Logs** tab.

If an Admin already saved a provider under Settings → AI in this database, that saved setting
wins over `LLM_PROVIDER` / `LLM_MODEL`; change it there.

The RAG service must not have `PYRAG_TEMPERATURE` set: `gpt-6-luna` accepts only its default
temperature and rejects any other value.

## 4. Check the service URLs

Open each service: the URL is under its name. If a name was taken, Render adds a suffix
(e.g. `bunonbrain-api-ab12.onrender.com`). Then fix:

| Wrong URL of | Fix on | Keys |
|---|---|---|
| frontend | api → Environment | `CORS_ORIGINS`, `PUBLIC_APP_URL` |
| api | web → Environment, then **Manual Deploy → Clear build cache & deploy** | `NEXT_PUBLIC_API_URL`, `DOTNET_API_URL` |
| rag | api → Environment | `PYRAG_SERVICE_URL` |

## 5. Load the PDFs into Qdrant Cloud

The new cluster is empty, and the old local vectors used a different embedding model, so
ingest again with Gemini embeddings. From your PC (PowerShell, in `rag/`; real environment
variables win over `.env`):

```powershell
$env:PYRAG_EMBEDDING_PROVIDER = "google"
$env:PYRAG_EMBEDDING_MODEL    = ""
$env:PYRAG_EMBEDDING_DIM      = ""
$env:PYRAG_QDRANT_URL         = "https://<cluster>.cloud.qdrant.io:6333"
$env:PYRAG_QDRANT_API_KEY     = "<qdrant key>"
$env:PYRAG_QDRANT_PATH        = ""
python ingest.py --pdf data/<your-file>.pdf --recreate
```

Run it once per PDF (`--recreate` only for the first one).

## 6. Computer vision in the factory (only while demoing)

On a PC in the factory network, in `computer-vision/.env`:

```
CV_BACKEND_URL=https://bunonbrain-api.onrender.com
CV_BACKEND_SERVICE_KEY=<value of CV_SERVICE_KEY from the API's Environment tab>
CV_API_KEY=<new random 32+ chars>
CV_ENABLE_PREVIEW=false
CV_ALLOW_SOURCE_OVERRIDE=false
```

Download the weights (`python scripts/download_model.py`) and start it as described in
`computer-vision/README.md`. It only makes outgoing HTTPS calls, so no router port is opened.
Stop it after the demo (see the 750-hour note in section 1).

## 7. IoT / MQTT (optional)

- HTTP device ingestion works as-is against `https://bunonbrain-api.onrender.com`.
- Render accepts only HTTP(S), so MQTT needs a hosted broker with TLS (HiveMQ Cloud / EMQX Cloud
  free tier). Set on the API: `Iot__Mqtt__Enabled=true`, `Iot__Mqtt__Host`, `Iot__Mqtt__Port=8883`,
  `Iot__Mqtt__UseTls=true`, `Iot__Mqtt__Username`, `Iot__Mqtt__Password`.

## 8. Check it works

1. Open `https://bunonbrain-api.onrender.com/health` first and wait for `Healthy` (this wakes the API).
2. Open the frontend and sign in with the seeded admin.
3. Ask BunonBrain a question about an ingested PDF (the first one may take a minute while the RAG service wakes).
4. The Overview page updates live.

**Before a demo:** open the API `/health`, the RAG `/health` and the frontend 2–3 minutes
early so nothing is asleep when people watch.

## 9. Things to know

- **Self-registration** is on by default: anyone can create an account (no role until an admin
  grants one). Set `ALLOW_SELF_REGISTRATION=false` on the API to stop that.
- **Password-reset email** needs `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM`.
- **Keeping uploaded files** needs a paid API with a disk: set `plan: starter` on
  `bunonbrain-api` and add back a `disk:` block (`mountPath: /app/App_Data`, `sizeGB: 1`),
  about $7.25/month. That also removes the API's sleep.
- **Never** put real values into `render.yaml` or commit `.env`.
