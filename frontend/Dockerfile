# Build the project
FROM node:alpine AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN npm install
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# Build the production image
FROM node:alpine
EXPOSE 3000
WORKDIR /nzb-webdav
ENV NODE_ENV=production
COPY --from=build-env /nzb-webdav/node_modules ./node_modules
COPY --from=build-env /nzb-webdav/package.json ./package.json
# Whole dist-node dir: server.js plus its compiled local imports (e.g. server-compression.js, issue #35)
COPY --from=build-env /nzb-webdav/dist-node ./dist-node
COPY --from=build-env /nzb-webdav/build ./build
CMD ["npm", "run", "start"]
