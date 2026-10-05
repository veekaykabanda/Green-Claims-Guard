# Green Claims Guard

Green Claims Guard, compliance tool designed for fashion retailers. It checks a sustainability or "green" claim in a product
description against real UK and EU advertising rules in real time, before the product is published.

## Why this exists

Fashion sustainability claims are becoming more common and under the UK's
DMCC Act 2024, the CMA can fine a retailer up to **10% of global turnover** for a misleading green
claim. Today main compliance check are manual and spot-check-only. Green Claims Guard aims to make this process more consistent by checking claims while they're being prepared for publicaton.

The rules used by the system are based on real advertising guidance and previous enforcement cases involving brands such as ASOS, Boohoo, George at Asda, H&M, Ryanair, Oatly, Alpro and Innocent Drinks. This assist the tool focus on the type of environmental claims that have caused problems rather than relying only on general AI responses.

## How it works

Two people use it and neither can do the other's job:

- **Copywriter** — writes the product copy. As they type, a free rule-engine pass flags known
  problem patterns instantly, and a slower AI pass catches nuanced wording the rules miss. The AI
  is kept on a short leash: capped at medium severity and a guard rejects any AI suggestion that
  invents a number not already in the text or the verified product facts.
- **Senior Editor** — reviews the flagged findings and the suggested rewrite, then signs off.
  Nothing publishes without this step. Every action lands in an insert-only audit ledger.

Sign-in and permissions are handled by Auth0; the backend trusts only the signed JWT, never a
client-supplied role.

## Tech stack

- **Frontend:** React (Create React App), `src/`
- **Backend:** .NET 10 minimal API, `backend-dotnet/GreenClaimsGuard.Api`
- **Database:** SQL Server / Azure SQL, via EF Core migrations
- **Auth:** Auth0 (JWT bearer)
- **AI:** OpenAI (`gpt-4o-mini`), optional — the app runs rules-only without a key
- **Deployment:** Docker, Azure App Service for Containers

## Running it locally

The whole app, one command, via Docker:

1. Copy `.env.example` to `.env` and fill in the required values (an Auth0 tenant's domain/client
   ID, and a database password — see the comments in the file for what's required vs optional).
2. ```
   docker compose up --build
   ```
3. Open the web app at **http://localhost:8081** (the API runs at **http://localhost:8080**).

The API creates and migrates its own database automatically on first start.

### Running without Docker

- **Backend:** from `backend-dotnet/GreenClaimsGuard.Api`, run `dotnet run` (needs a `.env` file in
  that folder, or the same environment variables set directly).
- **Frontend:** from the repo root, run `npm install` then `npm start` (serves on port 8081 by
  default, per `.env.development`).

## Live deployment

Deployed on Azure App Service for Containers:

- **Web app:** https://green-claims-ui.azurewebsites.net
- **API:** https://green-claims-api.azurewebsites.net (`/health` for a liveness check)

## Running the tests

- **Backend:** from `backend-dotnet`, run `dotnet test` (361 tests)
- **Frontend:** from the repo root, run `npm test` (143 tests)

## Project structure

```
src/                          React frontend
backend-dotnet/
  GreenClaimsGuard.Api/       .NET API — endpoints, services, EF Core migrations
  GreenClaimsGuard.Api.Tests/ Backend test suite
docs/                         Deployment plan, manual test log, legal citation audit
```

## License

MIT — see [LICENSE](LICENSE).
