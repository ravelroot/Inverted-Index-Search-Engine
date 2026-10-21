#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"

segredo() {
  LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-40}"
}

if [ ! -f .env ]; then
  cp .env.example .env
  echo "Arquivo .env criado a partir do .env.example."
fi

if grep -q '^SONDA_API_KEY=$' .env; then
  sed "s|^SONDA_API_KEY=$|SONDA_API_KEY=$(segredo 40)|" .env > .env.tmp && mv .env.tmp .env
fi

porta=$(grep '^SONDA_PORT=' .env | cut -d= -f2)
porta=${porta:-8080}
chave=$(grep '^SONDA_API_KEY=' .env | cut -d= -f2)

abrir() {
  tentativas=0
  until curl -fsS "http://127.0.0.1:${porta}/health" >/dev/null 2>&1; do
    tentativas=$((tentativas + 1))
    if [ "$tentativas" -ge 90 ]; then
      echo "O servidor não respondeu em 3 minutos."
      return 1
    fi
    sleep 2
  done

  echo "Painel:          http://localhost:${porta}"
  echo "Chave de acesso: ${chave}"
  if command -v xdg-open >/dev/null 2>&1; then
    xdg-open "http://localhost:${porta}" >/dev/null 2>&1 || true
  elif command -v open >/dev/null 2>&1; then
    open "http://localhost:${porta}" || true
  fi
}

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "Compilando com o .NET SDK instalado..."
  dotnet build src/Sonda.Server -c Release -nologo -v quiet
  abrir &
  Urls="http://127.0.0.1:${porta}" Sonda__Api__ApiKey="${chave}" exec dotnet src/Sonda.Server/bin/Release/net10.0/Sonda.Server.dll
elif command -v docker >/dev/null 2>&1; then
  docker compose up -d --build
  abrir
else
  echo "Instale o .NET SDK 10 (https://dot.net) ou o Docker e rode de novo."
  exit 1
fi
