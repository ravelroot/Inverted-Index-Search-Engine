import { api, parametros } from '../api.js';
import { navegar, estado } from '../app.js';
import { arvoreExplicacao, base64Url, carregando, el, fmt, fragmentos, icone, ponto, preencher, segmentado } from '../ui.js';

const DICAS = [
  ['motor busca', 'Todos os termos, com stemming: encontra "motores de buscas".'],
  ['cenoura OR chocolate', 'Qualquer um dos termos.'],
  ['"índice invertido"', 'Frase exata, respeitando a ordem.'],
  ['"motor relevancia"~4', 'Frase com até 4 posições de distância.'],
  ['busc*', 'Prefixo: busca, buscador, buscando…'],
  ['algoritimo~', 'Aproximada: tolera erros de digitação.'],
  ['bm25 -tfidf', 'Exclui documentos com o termo.'],
  ['titulo:farol', 'Só no campo título (também corpo, caminho).'],
  ['tipo:html idioma:en', 'Filtros por extensão, idioma e fonte.'],
  ['(feijoada OR moqueca) receita^2', 'Grupos e peso extra para um termo.'],
];

const EXEMPLOS = ['índice invertido', 'relevância bm25', 'farol', '"inverted index"', 'buracos negros', 'bolo cenoura', 'stemm*', 'tipo:html'];

const ROTULOS_FACETA = { ext: 'Tipo', lang: 'Idioma', source: 'Fonte' };
const NOMES_IDIOMA = { pt: 'Português', en: 'Inglês' };
const PARAMETRO_FACETA = { ext: 'tipo', lang: 'idioma', source: 'fonte' };

function lerEstado(parametrosRota) {
  return {
    q: parametrosRota.get('q') ?? '',
    pagina: Number(parametrosRota.get('pagina') ?? 1) || 1,
    ordem: parametrosRota.get('ordem') ?? 'relevancia',
    similaridade: parametrosRota.get('similaridade') ?? '',
    operador: parametrosRota.get('operador') ?? '',
    explicar: parametrosRota.get('explicar') === '1',
    tipo: parametrosRota.get('tipo') ?? '',
    idioma: parametrosRota.get('idioma') ?? '',
    fonte: parametrosRota.get('fonte') ?? '',
  };
}

export async function render(alvo, parametrosRota) {
  const atual = lerEstado(parametrosRota);
  const ir = (mudancas) => navegar('busca', { ...atual, pagina: 1, explicar: atual.explicar ? '1' : '', ...mudancas });

  const entrada = el('input', {
    type: 'search',
    name: 'q',
    value: atual.q,
    placeholder: 'Pesquise por palavras, "frases", prefixos* ou campos como titulo:farol',
    autocomplete: 'off',
    spellcheck: 'false',
    'aria-label': 'Consulta',
  });
  const formulario = el('form', { class: 'busca-barra', role: 'search', onsubmit: (e) => { e.preventDefault(); ir({ q: entrada.value.trim() }); } },
    el('label', { class: 'busca-entrada' }, icone('busca'), entrada),
    el('button', { class: 'botao', type: 'submit', text: 'Buscar' }));

  const opcoes = el('div', { class: 'busca-opcoes' },
    segmentado([['relevancia', 'Relevância'], ['recentes', 'Mais recentes']], atual.ordem, (v) => ir({ ordem: v }), 'Ordenar'),
    segmentado([['bm25', 'BM25'], ['tfidf', 'TF-IDF']], atual.similaridade || (estado.info?.similaridade === 'TF-IDF' ? 'tfidf' : 'bm25'), (v) => ir({ similaridade: v }), 'Modelo'),
    segmentado([['', 'Todos os termos'], ['ou', 'Qualquer termo']], atual.operador, (v) => ir({ operador: v }), 'Combinar'),
    el('label', { class: 'marcador' },
      el('input', { type: 'checkbox', checked: atual.explicar, onchange: (e) => navegar('busca', { ...atual, explicar: e.target.checked ? '1' : '' }) }),
      'Explicar pontuação'));

  const total = estado.info ? `${fmt.numero(estado.info.documentos)} documentos indexados` : 'Índice local';
  alvo.append(
    el('header', { class: 'cabecalho' },
      el('div', {}, el('h1', { text: 'Busca' }), el('p', { text: `${total}. Ranking por relevância com análise de texto em português e inglês.` }))),
    el('section', { class: 'painel painel-corpo' }, formulario, opcoes));

  const area = el('section');
  alvo.append(area);
  if (!atual.q) {
    entrada.focus();
    area.append(estadoVazio(ir));
    return null;
  }

  area.append(carregando('Buscando…'));
  const controle = new AbortController();
  try {
    const resposta = await api(`/busca${parametros({
      q: atual.q,
      pagina: atual.pagina,
      tamanho: 10,
      ordem: atual.ordem,
      similaridade: atual.similaridade,
      operador: atual.operador,
      explicar: atual.explicar ? 'true' : '',
      tipo: atual.tipo,
      idioma: atual.idioma,
      fonte: atual.fonte,
    })}`, { sinal: controle.signal });
    area.replaceChildren(...resultados(resposta, atual, ir));
  } catch (erro) {
    if (erro.name !== 'AbortError') {
      area.replaceChildren(el('div', { class: 'aviso-relaxado', text: erro.message }));
    }
  }

  const fecharComEsc = (evento) => {
    if (evento.key === 'Escape') {
      document.querySelector('.gaveta-fundo')?.click();
    }
  };
  document.addEventListener('keydown', fecharComEsc);
  return () => {
    controle.abort();
    document.removeEventListener('keydown', fecharComEsc);
    document.querySelectorAll('.gaveta, .gaveta-fundo').forEach((n) => n.remove());
  };
}

function estadoVazio(ir) {
  return el('div', { class: 'vazio', style: { marginTop: '22px' } },
    el('div', { class: 'painel' },
      el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Como pesquisar' }), el('p', { text: 'A sintaxe é parecida com a do Lucene, com operadores também em português (E, OU, NAO).' }))),
      el('div', { class: 'dicas' }, DICAS.map(([codigo, texto]) => el('div', { class: 'dica' }, el('code', { text: codigo }), el('span', { text: texto }))))),
    el('div', {},
      el('p', { class: 'mudo', style: { marginBottom: '8px' }, text: 'Experimente:' }),
      el('div', { class: 'exemplos' }, EXEMPLOS.map((exemplo) => el('button', { class: 'botao secundario pequeno', type: 'button', text: exemplo, onclick: () => ir({ q: exemplo }) })))));
}

function resultados(resposta, atual, ir) {
  const totalTexto = resposta.totalIsLowerBound ? `${fmt.numero(resposta.total)}+` : fmt.numero(resposta.total);
  const blocos = [
    el('div', { class: 'resumo' },
      el('strong', { text: `${totalTexto} resultado${resposta.total === 1 ? '' : 's'}` }),
      el('span', { class: 'mudo', text: `em ${fmt.decimal(resposta.elapsedMs, 1)} ms · ${resposta.similarity}${atual.ordem === 'recentes' ? ' · mais recentes primeiro' : ''}` })),
    el('code', { class: 'consulta-interpretada mono', title: 'Consulta depois da análise de texto', text: resposta.parsedQuery }),
  ];
  if (resposta.relaxed) {
    blocos.push(el('div', { class: 'aviso-relaxado', text: 'Nenhum documento tinha todos os termos, então a busca foi ampliada para qualquer um deles.' }));
  }

  const lista = el('div', { class: 'resultados' });
  if (resposta.hits.length === 0) {
    lista.append(el('div', { class: 'painel vazio-painel', text: 'Nenhum documento encontrado. Tente menos palavras, um prefixo (busc*) ou uma busca aproximada (palavra~).' }));
  }

  const posicaoInicial = (resposta.page - 1) * resposta.pageSize;
  resposta.hits.forEach((hit, indice) => lista.append(cartao(hit, posicaoInicial + indice + 1, atual)));

  const paginas = Math.ceil(Math.min(resposta.total, 1000) / resposta.pageSize);
  if (paginas > 1) {
    lista.append(el('nav', { class: 'paginacao', 'aria-label': 'Páginas' },
      el('button', { class: 'botao secundario pequeno', type: 'button', disabled: resposta.page <= 1, text: 'Anterior', onclick: () => navegar('busca', { ...atual, explicar: atual.explicar ? '1' : '', pagina: resposta.page - 1 }) }),
      el('span', { text: `Página ${resposta.page} de ${fmt.numero(paginas)}` }),
      el('button', { class: 'botao secundario pequeno', type: 'button', disabled: resposta.page >= paginas, text: 'Próxima', onclick: () => navegar('busca', { ...atual, explicar: atual.explicar ? '1' : '', pagina: resposta.page + 1 }) })));
  }

  blocos.push(el('div', { class: 'grade-busca' }, facetas(resposta.facets, atual, ir), lista));
  return blocos;
}

function facetas(dados, atual, ir) {
  const grupos = ['ext', 'lang', 'source'].map((campo) => {
    const valores = Object.entries(dados?.[campo] ?? {});
    const parametro = PARAMETRO_FACETA[campo];
    const selecionado = atual[parametro];
    if (valores.length === 0 && !selecionado) {
      return null;
    }
    const itens = valores.length > 0 ? valores : [[selecionado, 0]];
    return el('div', { class: 'faceta' },
      el('h3', { text: ROTULOS_FACETA[campo] }),
      itens.map(([valor, contagem]) => el('button', {
        type: 'button',
        'aria-pressed': String(selecionado === valor),
        onclick: () => ir({ [parametro]: selecionado === valor ? '' : valor }),
      },
      el('span', { class: 'rotulo' }, campo === 'source' ? null : ponto(valor), campo === 'lang' ? (NOMES_IDIOMA[valor] ?? valor) : valor),
      el('span', { class: 'contagem', text: fmt.numero(contagem) }))));
  });
  return el('aside', { class: 'facetas', 'aria-label': 'Filtros' }, grupos);
}

function cartao(hit, posicao, atual) {
  const explicacao = hit.explanation ? arvoreExplicacao(hit.explanation) : null;
  return el('article', { class: 'resultado' },
    el('div', { class: 'resultado-topo' },
      el('div', {},
        el('h2', {}, el('button', { type: 'button', onclick: () => abrirDocumento(hit, atual.q) }, fragmentos(hit.titleFragments))),
        el('div', { class: 'meta' },
          el('span', { class: 'mono', text: `${hit.source}:${hit.path}` }),
          el('span', { class: 'etiqueta' }, ponto(hit.extension), hit.extension || 'sem extensão'),
          el('span', { class: 'etiqueta' }, ponto(hit.language), NOMES_IDIOMA[hit.language] ?? hit.language),
          el('span', { text: fmt.bytes(hit.size) }),
          el('span', { text: `modificado em ${fmt.data(hit.modified)}` }))),
      el('div', { class: 'pontuacao', title: 'Pontuação de relevância' }, el('strong', { text: fmt.decimal(hit.score, 3) }), el('span', { text: `#${posicao}` }))),
    hit.snippet?.length ? el('p', { class: 'trecho' }, fragmentos(hit.snippet)) : null,
    explicacao);
}

async function abrirDocumento(hit, consulta) {
  document.querySelectorAll('.gaveta, .gaveta-fundo').forEach((n) => n.remove());
  const corpo = el('div', { class: 'gaveta-corpo' }, carregando());
  const fechar = () => {
    fundo.remove();
    gaveta.remove();
  };
  const fundo = el('div', { class: 'gaveta-fundo', onclick: fechar });
  const gaveta = el('aside', { class: 'gaveta', role: 'dialog', 'aria-modal': 'true', 'aria-label': hit.title },
    el('div', { class: 'gaveta-topo' },
      el('div', {},
        el('h2', {}, fragmentos(hit.titleFragments)),
        el('div', { class: 'meta' },
          el('span', { class: 'mono', text: `${hit.source}:${hit.path}` }),
          el('span', { text: fmt.bytes(hit.size) }),
          el('span', { text: fmt.dataHora(hit.modified) }))),
      el('button', { class: 'icone-botao', type: 'button', 'aria-label': 'Fechar', onclick: fechar }, icone('fechar'))),
    corpo);
  document.body.append(fundo, gaveta);
  gaveta.querySelector('button').focus();
  try {
    const visao = await api(`/documentos/${base64Url(hit.key)}${parametros({ q: consulta })}`);
    preencher(corpo,
      visao.document.description ? el('p', { class: 'mudo', style: { marginBottom: '14px' }, text: visao.document.description }) : null,
      fragmentos(visao.body),
      visao.truncated ? el('p', { class: 'mudo', style: { marginTop: '14px' }, text: `Mostrando os primeiros 60 mil de ${fmt.numero(visao.length)} caracteres.` }) : null);
  } catch (erro) {
    corpo.replaceChildren(el('p', { class: 'mudo', text: erro.message }));
  }
}
