let chave = null;

export function definirChave(valor) {
  chave = valor ? valor.trim() : null;
}

export function temChave() {
  return Boolean(chave);
}

export async function api(caminho, { metodo = 'GET', corpo, sinal } = {}) {
  const cabecalhos = { Accept: 'application/json' };
  if (corpo !== undefined) {
    cabecalhos['Content-Type'] = 'application/json';
  }
  if (chave) {
    cabecalhos['X-Sonda-Key'] = chave;
  }

  const resposta = await fetch(`api${caminho}`, {
    method: metodo,
    headers: cabecalhos,
    body: corpo === undefined ? undefined : JSON.stringify(corpo),
    signal: sinal,
  });

  if (resposta.status === 204) {
    return null;
  }

  const tipo = resposta.headers.get('content-type') ?? '';
  const dados = tipo.includes('json') ? await resposta.json() : null;
  if (!resposta.ok) {
    const erro = new Error(dados?.detail ?? dados?.title ?? `Erro ${resposta.status}`);
    erro.status = resposta.status;
    throw erro;
  }

  return dados;
}

export function parametros(objeto) {
  const busca = new URLSearchParams();
  for (const [nome, valor] of Object.entries(objeto)) {
    if (valor !== undefined && valor !== null && valor !== '') {
      busca.set(nome, String(valor));
    }
  }
  const texto = busca.toString();
  return texto ? `?${texto}` : '';
}
