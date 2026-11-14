# Web image: build the Angular app, serve it from nginx which also reverse-proxies /api and /hubs to the API.
FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/kshethra-web/browser /usr/share/nginx/html
EXPOSE 80
