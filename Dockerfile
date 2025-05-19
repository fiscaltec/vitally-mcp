# Dockerfile
FROM node:23-alpine

WORKDIR /app

COPY . .

RUN npm install
RUN npm run build

CMD ["node", "build/index.js"]
