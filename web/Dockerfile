FROM node:22-slim AS base
RUN corepack enable && corepack prepare pnpm@latest --activate
WORKDIR /app

FROM base AS deps
COPY package.json pnpm-lock.yaml .npmrc ./
RUN pnpm install --frozen-lockfile

FROM base
COPY --from=deps /app/node_modules ./node_modules
COPY . .
EXPOSE 5557
CMD ["pnpm", "dev", "--host"]
