import { api, parametros } from '../api.js';
import { cabecalho, carregando, copiar, el, icone } from '../ui.js';

const EVENTOS = { execucao: 'indexação concluída', indice: 'índice alterado' };

function endereco(caminho) {
  return new URL(caminho, location.href).href;
}

function linhaEndereco(url, rotulo) {
  return el('div', { class: 'endereco' },
    el('span', { text: url, title: url }),
    el('button', { class: 'mini', type: 'button', title: `Copiar ${rotulo}`, 'aria-label': `Copiar ${rotulo}`, onclick: () => copiar(url, `${rotulo[0].toUpperCase()}${rotulo.slice(1)} copiado.`) }, icone('copiar')));
}

function cartaoIntegracao({ iconeNome, titulo, descricao, selo, corpo, acoes }) {
  return el('section', { class: 'painel integracao' },
    el('div', { class: 'painel-cabecalho' },
      el('div', { style: { display: 'flex', gap: '12px', alignItems: 'flex-start' } },
        el('span', { class: 'integracao-icone' }, icone(iconeNome)),
        el('div', {}, el('h2', { text: titulo }), el('p', { text: descricao }))),
      selo ?? null),
    el('div', { class: 'painel-corpo' }, corpo),
    acoes?.length ? el('div', { class: 'rodape' }, acoes) : null);
}

function exemplos(consulta, info) {
  const base = endereco('.').replace(/\/$/, '');
  const busca = `${base}/api/busca${parametros({ q: consulta, tamanho: 5 })}`;
  const chave = info.protegido ? '\n  -H "X-Sonda-Key: $SONDA_CHAVE"' : '';
  return [
    ['curl', `curl -s "${busca}" | jq '.hits[] | {title, score, path}'\n\n# reindexar uma fonte (exige a chave quando houver)\ncurl -X POST "${base}/api/fontes/exemplos/indexar?modo=incremental"${chave}`],
    ['PowerShell', `$r = Invoke-RestMethod "${busca}"\n$r.hits | Select-Object title, score, path\n\n# exportar para Excel\nInvoke-WebRequest "${base}/api/busca/exportar${parametros({ q: consulta, formato: 'csv' })}" -OutFile sonda.csv`],
    ['JavaScript', `const resposta = await fetch(${JSON.stringify(busca)});\nconst { total, hits } = await resposta.json();\nfor (const hit of hits) {\n  console.log(hit.score.toFixed(3), hit.title, hit.path);\n}\n\n// eventos em tempo real\nconst eventos = new EventSource(${JSON.stringify(`${base}/api/eventos`)});\neventos.addEventListener('execucao', (e) => console.log(JSON.parse(e.data)));`],
    ['Python', `import requests\n\nr = requests.get(${JSON.stringify(`${base}/api/busca`)}, params={"q": ${JSON.stringify(consulta)}, "tamanho": 5})\nfor hit in r.json()["hits"]:\n    print(f"{hit['score']:.3f}  {hit['title']}  {hit['path']}")`],
  ];
}

function blocoCodigo(consultaInicial, info) {
  let consulta = consultaInicial;
  let aba = 0;
  const pre = el('pre', { class: 'codigo' });
  const abas = el('div', { class: 'abas', role: 'tablist' });
  const entrada = el('input', { type: 'search', value: consulta, 'aria-label': 'Consulta dos exemplos', placeholder: 'Consulta usada nos exemplos', style: { flex: '1' } });
  const desenhar = () => {
    const lista = exemplos(consulta || 'índice invertido', info);
    abas.replaceChildren(...lista.map(([nome], i) => el('button', {
      type: 'button',
      role: 'tab',
      'aria-selected': String(i === aba),
      text: nome,
      onclick: () => {
        aba = i;
        desenhar();
      },
    })));
    pre.textContent = lista[aba][1];
  };
  entrada.addEventListener('input', () => {
    consulta = entrada.value.trim();
    desenhar();
  });
  desenhar();
  return el('section', { class: 'painel', style: { marginTop: '16px' } },
    el('div', { class: 'painel-cabecalho' },
      el('div', {}, el('h2', { text: 'API REST em qualquer linguagem' }), el('p', { text: 'Exemplos prontos com o endereço deste servidor. Mude a consulta e copie.' })),
      el('button', { class: 'botao secundario pequeno', type: 'button', onclick: () => copiar(pre.textContent, 'Exemplo copiado.') }, icone('copiar'), 'Copiar')),
    el('div', { style: { display: 'flex', gap: '8px', padding: '12px 18px', borderBottom: '1px solid var(--linha)' } }, entrada),
    abas,
    pre);
}

export async function render(alvo, parametrosRota) {
  const consulta = parametrosRota.get('q') ?? 'índice invertido';
  alvo.append(
    cabecalho('Conexões', 'Integrações', 'Leve a Sonda para o navegador, para o leitor de feeds, para planilhas, para o monitoramento e para os seus sistemas, sem instalar nada além do próprio servidor.'),
    carregando());

  let dados;
  let info;
  try {
    [dados, info] = await Promise.all([api('/integracoes'), api('/info')]);
  } catch (erro) {
    alvo.replaceChildren(alvo.firstChild, el('div', { class: 'aviso-relaxado', text: erro.message }));
    return null;
  }

  const feed = endereco(`api/busca/feed${parametros({ q: consulta })}`);
  const opensearch = endereco(dados.opensearch.replace(/^\//, ''));
  const metricas = endereco(dados.metricas.replace(/^\//, ''));
  const webhooks = dados.webhooks ?? { quantidade: 0, eventos: [] };

  const cartoes = el('div', { class: 'integracoes' },
    cartaoIntegracao({
      iconeNome: 'navegador',
      titulo: 'Navegador',
      descricao: 'Pesquise na Sonda pela barra de endereços, com sugestões enquanto digita.',
      selo: el('span', { class: 'selo ativo', text: 'OpenSearch' }),
      corpo: [
        el('p', { text: 'No Chrome e no Edge, abra uma vez este painel e procure “Sonda” em Configurações › Mecanismos de pesquisa. No Firefox, clique com o botão direito na barra de endereços e escolha “Adicionar Sonda”.' }),
        linhaEndereco(opensearch, 'endereço OpenSearch'),
      ],
      acoes: [el('a', { class: 'botao secundario pequeno', href: dados.opensearch.replace(/^\//, ''), target: '_blank', rel: 'noopener' }, icone('documento'), 'Ver descrição')],
    }),
    cartaoIntegracao({
      iconeNome: 'feed',
      titulo: 'Feed Atom',
      descricao: 'Acompanhe uma busca num leitor de feeds: documentos novos aparecem sozinhos.',
      selo: el('span', { class: 'selo ativo', text: 'Atom 1.0' }),
      corpo: [
        el('p', { text: 'Qualquer busca vira feed, com os mesmos filtros de tipo, idioma e fonte. Os 20 melhores resultados entram como itens.' }),
        linhaEndereco(feed, 'endereço do feed'),
      ],
      acoes: [el('a', { class: 'botao secundario pequeno', href: `api/busca/feed${parametros({ q: consulta })}`, target: '_blank', rel: 'noopener' }, icone('feed'), 'Abrir feed')],
    }),
    cartaoIntegracao({
      iconeNome: 'tabela',
      titulo: 'Planilhas e dados',
      descricao: 'Exporte até 1.000 resultados para Excel, Power BI, pandas ou jq.',
      selo: el('span', { class: 'selo ativo', text: dados.exportar.map((f) => f.toUpperCase()).join(' · ') }),
      corpo: [
        el('p', { text: 'CSV em UTF-8 com BOM (abre direto no Excel) e proteção contra fórmulas, JSON com o envelope completo ou NDJSON, um resultado por linha.' }),
        linhaEndereco(endereco(`api/busca/exportar${parametros({ q: consulta, formato: 'csv' })}`), 'endereço de exportação'),
      ],
      acoes: dados.exportar.map((formato) => el('a', { class: 'botao secundario pequeno', href: `api/busca/exportar${parametros({ q: consulta, formato })}`, download: '' }, icone('baixar'), formato.toUpperCase())),
    }),
    cartaoIntegracao({
      iconeNome: 'pulso',
      titulo: 'Monitoramento',
      descricao: 'Métricas no formato do Prometheus, prontas para Grafana e alertas.',
      selo: el('span', { class: 'selo ativo', text: 'Prometheus' }),
      corpo: [
        el('p', { text: 'Documentos, segmentos, tamanho do índice, buscas, buscas sem resultado, latência e execuções de indexação.' }),
        linhaEndereco(metricas, 'endereço das métricas'),
        el('pre', { class: 'codigo', style: { borderRadius: 'var(--raio)' }, text: `scrape_configs:\n  - job_name: sonda\n    static_configs:\n      - targets: ["${location.host}"]` }),
      ],
      acoes: [el('a', { class: 'botao secundario pequeno', href: dados.metricas.replace(/^\//, ''), target: '_blank', rel: 'noopener' }, icone('pulso'), 'Ver métricas')],
    }),
    cartaoIntegracao({
      iconeNome: 'gancho',
      titulo: 'Webhooks',
      descricao: 'Avise outros sistemas quando uma indexação terminar ou o índice mudar.',
      selo: el('span', { class: `selo ${webhooks.quantidade > 0 ? 'ativo' : ''}`, text: webhooks.quantidade > 0 ? `${webhooks.quantidade} destino(s)` : 'Desligado' }),
      corpo: [
        el('dl', { class: 'lista-def' },
          el('dt', { text: 'Eventos' }), el('dd', { text: (webhooks.eventos ?? []).map((e) => `${e} (${EVENTOS[e] ?? e})`).join(', ') || '—' }),
          el('dt', { text: 'Assinatura' }), el('dd', { text: webhooks.assinado ? 'HMAC-SHA256 em X-Sonda-Assinatura' : 'sem segredo definido' }),
          el('dt', { text: 'Configuração' }), el('dd', { text: 'Sonda:Webhooks:Urls, Secret e Events' })),
        el('p', { class: 'mudo', style: { fontSize: 'var(--t-xs)' }, text: 'Cada chamada é um POST JSON { evento, momento, dados } com o cabeçalho X-Sonda-Evento.' }),
      ],
    }),
    cartaoIntegracao({
      iconeNome: 'api',
      titulo: 'API e eventos',
      descricao: 'REST com Problem Details e eventos em tempo real por Server-Sent Events.',
      selo: el('span', { class: `selo ${dados.protegido ? 'ativo' : ''}`, text: dados.protegido ? 'Escrita com chave' : 'Sem chave' }),
      corpo: [
        el('dl', { class: 'lista-def' },
          el('dt', { text: 'Versão' }), el('dd', { text: dados.versao }),
          el('dt', { text: 'Busca' }), el('dd', { text: 'GET /api/busca' }),
          el('dt', { text: 'Sugestões' }), el('dd', { text: 'GET /api/sugestoes' }),
          el('dt', { text: 'Semelhantes' }), el('dd', { text: 'GET /api/documentos/{chave}/semelhantes' }),
          el('dt', { text: 'Eventos' }), el('dd', { text: 'GET /api/eventos' })),
      ],
      acoes: [el('a', { class: 'botao secundario pequeno', href: 'api/info', target: '_blank', rel: 'noopener' }, icone('api'), 'api/info')],
    }));

  alvo.replaceChildren(alvo.firstChild, cartoes, blocoCodigo(consulta, info));
  return null;
}
