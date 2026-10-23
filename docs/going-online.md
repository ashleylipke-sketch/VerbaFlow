# Putting VerbaFlow online

This puts VerbaFlow in your own Azure subscription, so colleagues can use it from their own computers with their Microsoft work accounts. You run one script in **Azure Cloud Shell**, which is a command window inside the Azure website, so there is nothing to install.

Before you start, you need:
- Microsoft sign-in set up and working on your computer (`entra-sign-in-setup.md`).
- Your Azure AI Speech and Azure OpenAI resources (the ones VerbaFlow already uses).

## What it creates and costs

Everything goes into one resource group called **verbaflow**, in the region you choose (`southafricanorth` or `uksouth`).

| Part | What it is for | Rough cost a month |
|---|---|---|
| Container App `verbaflow` (one copy, 1 CPU, 2 GB, always on) | VerbaFlow itself: pages, sign-in, processing, live speech | $25 to $70 |
| PostgreSQL Flexible Server, Burstable B1ms, 32 GB, 7 days of backups | Items, transcripts, history, settings | about $18 to $20 |
| Storage account with a file share `verbaflow-data` | Recordings, letterheads, the speaker model | $1 to $5 |
| Container Registry, Basic | Holds each version of VerbaFlow | about $5 |
| Log Analytics, 30 days | Server log (support detail) | $0 to $3 |

That is about **$50 to $100 a month**, plus Speech and OpenAI use as today. Prices are Microsoft's list prices in US dollars, checked in October 2026. The Azure pricing calculator gives the exact figure for your account.

The keys for Speech, OpenAI and the database are kept as secrets of the Container App. They are never shown, and nobody types them.

## 1. Open Azure Cloud Shell

1. Go to https://portal.azure.com and sign in as you do for your Azure resources.
2. Click the **Cloud Shell** button at the top (a `>_` icon).
3. If it asks, choose **Bash**. If it asks about storage, choose **No storage account required** and pick your subscription.

## 2. Get VerbaFlow into Cloud Shell

Type these one at a time:

```
gh auth login
```

Answer: **GitHub.com**, **HTTPS**, **Login with a web browser**. It shows a code: open the address it gives, sign in to GitHub and enter the code. Then:

```
gh repo clone ashleylipke-sketch/VerbaFlow
cd VerbaFlow
```

## 3. Run the setup

For South Africa:

```
bash deploy/azure-setup.sh southafricanorth
```

For the United Kingdom, use `uksouth` instead. Choose carefully: moving real data between regions later is a separate job.

It takes about 15 to 20 minutes the first time, most of it creating the database and building VerbaFlow. It prints each step. At the end it shows VerbaFlow's web address, for example:

```
VerbaFlow is online at:

    https://verbaflow.<letters>.southafricanorth.azurecontainerapps.io/
```

The script also adds that address to the VerbaFlow app registration, so Microsoft sign-in works there.

If you have more than one Speech or OpenAI resource, the script stops and tells you to name one, like this:

```
SPEECH_NAME=my-speech OPENAI_NAME=my-openai bash deploy/azure-setup.sh southafricanorth
```

If your OpenAI deployment is not called `gpt-4.1-mini`, put `OPENAI_DEPLOYMENT=<its name>` in front in the same way.

## 4. Sign in online

1. Open the address.
2. Click **Sign in with Microsoft**.
3. You should see your name and **(admin)** at the top right.

The test users (Alice, Bob and the others) do not exist online. Items on your computer stay on your computer.

## 5. Point the Widget at it

In Chrome, click the VerbaFlow Widget's icon to open its settings, put the new address in **VerbaFlow address**, and click **Save and check**. Sign in with Microsoft again if it asks.

## 6. Let colleagues in

- In the Entra admin center, under **Enterprise apps › VerbaFlow › Users and groups**, add each colleague, as in step 6 of `entra-sign-in-setup.md`. Give **VerbaFlow administrator** only to the people who should be admins (VerbaFlow needs two for reopening and deleting approved items).
- Send them the address. They sign in with their own Microsoft work account.

## 7. Set a budget alert (recommended)

In the Azure portal, search for **Budgets**, click **+ Add**, set the scope to the **verbaflow** resource group, an amount such as $120 a month, and your email for alerts at 80% and 100%.

## Updating to a newer version

When I tell you there is a new version, open Cloud Shell and type:

```
gh auth login
[ -d VerbaFlow ] || gh repo clone ashleylipke-sketch/VerbaFlow
cd VerbaFlow
bash deploy/azure-update.sh
```

(`gh auth login` is needed again each time Cloud Shell starts fresh; answer as in step 2.)

Anyone dictating at that moment loses what they have not saved, so pick a quiet moment. It takes a few minutes.

## If something goes wrong

- **The page does not load after the script finished:** wait five minutes and try again. The first start downloads the speaker model.
- **Sign-in says the reply address does not match:** in the Entra admin center, open **App registrations › VerbaFlow › Authentication**, click **Add Redirect URI**, choose **Single-page application** and add the exact address the script printed, ending with `/`.
- **Anything else:** send me the last lines the script printed (they never contain a key or password).

## For whoever looks after VerbaFlow

- The image is built from `Dockerfile` (web build, `dotnet publish`, then the ASP.NET 10 runtime with ffmpeg, OpenSSL and ALSA for the Speech SDK). Port 8080, `ASPNETCORE_ENVIRONMENT=Production` (so test users are off), data folder `/data`.
- `Database:ConnectionString` switches from SQLite to PostgreSQL (`PostgresDatabase`). It keeps the same tables and the same rules, enforced by triggers: audit events, media records and export fingerprints cannot be changed, removed or truncated, and an approved item can change only to deleted. Set `VERBAFLOW_TEST_POSTGRES` to a connection string to run the whole test suite on PostgreSQL.
- `/data` is the Azure Files share: recordings (write-once by VerbaFlow's own rules), letterheads, the downloaded speaker models and the email outbox.
- One copy runs (min and max replicas 1), because live speech sessions are held in memory. During an update a second copy runs for a moment; the audit log takes a table lock so entry numbers never clash.
- `GET /api/health` (public) answers 200 once the database can be reached; it is the liveness probe.
- The database accepts connections from Azure services only (`--public-access 0.0.0.0`), with a password and TLS. Later hardening: a private network, customer-managed keys, and Blob Storage immutability for recordings (decision 05).
