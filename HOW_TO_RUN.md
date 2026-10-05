# How to Run – Green Claims Guard (Localhost)

## Prerequisites
- Node.js 20+
- .NET 8 SDK
- OpenAI API key
- Auth0 account (free tier works)

---

## 1. Backend

```bash
cd backend-dotnet/GreenClaimsGuard.Api
```

Fill in your credentials in the `.env` file:

```
OPENAI_API_KEY=your-openai-key
AUTH0_DOMAIN=your-auth0-domain
AUTH0_AUDIENCE=https://greenclaims-api
```

Then run:

```bash
dotnet run
```

Backend starts on **http://localhost:8080**

---

## 2. Frontend

Fill in your credentials in `.env.development`:

```
REACT_APP_AUTH0_DOMAIN=your-auth0-domain
REACT_APP_AUTH0_CLIENT_ID=your-auth0-client-id
REACT_APP_AUTH0_AUDIENCE=https://greenclaims-api
```

From the project root:

```bash
npm install
npm start
```

Frontend starts on **http://localhost:8081**

---

## 3. Open the app

Go to **http://localhost:8081** in your browser and log in via Auth0.

---

## 4. Or run everything with Docker

With Docker installed, one command starts the database, the API and the web app:

1. Copy `.env.example` to `.env` and fill it in (a database password, and your Auth0 domain and client ID).
2. Run `docker compose up --build`.
3. Open **http://localhost:8081**.

The API creates its own database on first start. Change `API_PORT` or `WEB_PORT` in `.env` if 8080 or 8081 are
already in use. See `docs/deployment-plan.md` for what runs and how it would be deployed.
