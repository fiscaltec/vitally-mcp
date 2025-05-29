# Dockerfile
FROM node:23-alpine

WORKDIR /app

COPY . .

RUN npm install
RUN npm run build

CMD ["node", "dist/index.js"]
