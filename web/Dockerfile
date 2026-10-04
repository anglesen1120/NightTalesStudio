# syntax=docker/dockerfile:1.7
#
# NightTales web — deploy image (CI build + smoke, staging, production). Daily dev is
# native (`npm start`), not this image (10 CR-43).
#
# Base images are pinned by digest only. Human-readable tags (recorded in the PR body):
#   build   : node:26-bookworm-slim
#   runtime : nginxinc/nginx-unprivileged:1.29-alpine

# ---- build ----------------------------------------------------------------------
FROM node:26-bookworm-slim@sha256:662933cf47f013bc8e4beb31a6116448427a82057ba7c42c97e4c5ba766504c2 AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# csp:gen (10.1.6-T1) writes dist/nginx/csp-headers.conf, outside the served directory.
# If it is missing the build fails here by design: never ship an image without the CSP.
RUN npm run build && npm run csp:gen

# ---- runtime --------------------------------------------------------------------
FROM nginxinc/nginx-unprivileged@sha256:0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6
ENV NTS_SPA_FALLBACK=index.csr.html \
    NTS_CSP_MEDIA_ORIGINS="" \
    NTS_HSTS=""
# The base image ships stock index.html ("Welcome to nginx!") and 50x.html in
# /usr/share/nginx/html; the Angular output has no index.html (the fallback is
# index.csr.html), so the stock file would be served for "/" and "/index.html".
# Remove both before the app copy; the smoke asserts they are gone (D1).
USER root
RUN rm -f /usr/share/nginx/html/index.html /usr/share/nginx/html/50x.html
USER 101
COPY --from=build /app/dist/web/browser /usr/share/nginx/html
COPY --from=build /app/dist/nginx/csp-headers.conf /etc/nginx/templates/includes/csp-headers.conf.template
COPY nginx/templates/ /etc/nginx/templates/
COPY nginx/guards/ /etc/nginx/guards/
COPY --chmod=0755 docker-entrypoint.d/ /docker-entrypoint.d/
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
