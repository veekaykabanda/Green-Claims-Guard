# The Green Claims Guard web app: built with Node, then served as plain files by nginx.
#   docker build -t greenclaims-web \
#     --build-arg REACT_APP_API_URL=http://localhost:8080 \
#     --build-arg REACT_APP_AUTH0_DOMAIN=your-tenant.uk.auth0.com \
#     --build-arg REACT_APP_AUTH0_CLIENT_ID=your-client-id .
#
# A React app reads its settings when it is BUILT, not when it starts, so they are build arguments. They are
# public identifiers (the address of the API and of the Auth0 tenant), never secrets.

FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY public ./public
COPY src ./src

ARG REACT_APP_API_URL
ARG REACT_APP_AUTH0_DOMAIN
ARG REACT_APP_AUTH0_CLIENT_ID
ARG REACT_APP_AUTH0_AUDIENCE=https://greenclaims-api
ENV REACT_APP_API_URL=$REACT_APP_API_URL \
    REACT_APP_AUTH0_DOMAIN=$REACT_APP_AUTH0_DOMAIN \
    REACT_APP_AUTH0_CLIENT_ID=$REACT_APP_AUTH0_CLIENT_ID \
    REACT_APP_AUTH0_AUDIENCE=$REACT_APP_AUTH0_AUDIENCE

RUN npm run build

FROM nginx:1.27-alpine AS runtime
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/build /usr/share/nginx/html
EXPOSE 80
