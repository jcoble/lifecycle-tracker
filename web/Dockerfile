FROM node:22-slim
RUN corepack enable && corepack prepare pnpm@8.7.5 --activate
WORKDIR /app
COPY package.json pnpm-lock.yaml .npmrc ./
RUN pnpm install --frozen-lockfile
COPY . .
EXPOSE 5557
CMD ["pnpm", "dev", "--host"]
