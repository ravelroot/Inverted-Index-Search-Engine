import { api, parametros } from '../api.js';
import { cabecalho, carregando, el, fmt, segmentado, tabela } from '../ui.js';

const CAMPOS = [['body', 'Corpo'], ['title', 'Título'], ['path', 'Caminho'], ['ext', 'Tipo'], ['lang', 'Idioma'], ['source', 'Fonte']];

export async function render(alvo, parametrosRota) {
  const estadoLocal = {
    campo: parametrosRota.get('campo') ?? 'body',
    prefixo: parametrosRota.get('prefixo') ?? '',
    ordem: 'frequencia',
    termo: parametrosRota.get('termo') ?? '',
  };

  const lista = el('div', { class: 'lista-termos' }, carregando());
  const detalhe = el('section', { class: 'painel' });
  const campo = el('select', { 'aria-label': 'Campo' }, CAMPOS.map(([valor, texto]) => el('option', { value: valor, selected: valor === estadoLocal.campo, text: texto })));
  const prefixo = el('input', { type: 'search', value: estadoLocal.prefixo, placeholder: 'Prefixo', 'aria-label': 'Prefixo' });
  const ordemArea = el('span');

  const desenharOrdem = () => ordemArea.replaceChildren(segmentado([['frequencia', 'Mais frequentes'], ['alfabetica', 'A–Z']], estadoLocal.ordem, (v) => {
    estadoLocal.ordem = v;
    desenharOrdem();
    carregarTermos();
  }));

  let tempo = null;
  campo.addEventListener('change', () => {
    estadoLocal.campo = campo.value;
    carregarTermos();
  });
  prefixo.addEventListener('input', () => {
    clearTimeout(tempo);
    tempo = setTimeout(() => {
      estadoLocal.prefixo = prefixo.value.trim();
      carregarTermos();
    }, 180);
  });

  async function carregarTermos() {
    try {
      const termos = await api(`/termos${parametros({ campo: estadoLocal.campo, prefixo: estadoLocal.prefixo, limite: 80, ordem: estadoLocal.ordem })}`);
      lista.replaceChildren(tabela(
        ['Termo', { texto: 'df', num: true }, { texto: 'Ocorrências', num: true }, { texto: 'IDF', num: true }],
        termos.map((t) => el('tr', {
          class: 'clicavel',
          'aria-selected': String(t.term === estadoLocal.termo),
          onclick: (e) => {
            estadoLocal.termo = t.term;
            lista.querySelectorAll('tr[aria-selected]').forEach((tr) => tr.setAttribute('aria-selected', 'false'));
            e.currentTarget.setAttribute('aria-selected', 'true');
            carregarPostagens();
          },
        },
        el('td', { class: 'mono', text: t.term }),
        el('td', { class: 'num', text: fmt.numero(t.docFreq) }),
        el('td', { class: 'num', text: fmt.numero(t.totalTermFreq) }),
        el('td', { class: 'num', text: fmt.decimal(t.idf, 3) }))),
        { vazio: 'Nenhum termo com esse prefixo.' }));
      if (!estadoLocal.termo && termos.length > 0) {
        estadoLocal.termo = termos[0].term;
        lista.querySelector('tbody tr')?.setAttribute('aria-selected', 'true');
      }
      if (estadoLocal.termo && !detalhe.hasChildNodes()) {
        carregarPostagens();
      }
    } catch (erro) {
      lista.replaceChildren(el('div', { class: 'vazio-painel', text: erro.message }));
    }
  }

  async function carregarPostagens() {
    detalhe.replaceChildren(carregando());
    try {
      const p = await api(`/termos/postagens${parametros({ campo: estadoLocal.campo, termo: estadoLocal.termo, limite: 40 })}`);
      detalhe.replaceChildren(
        el('div', { class: 'painel-cabecalho' },
          el('div', {}, el('h2', {}, 'Lista invertida de ', el('span', { class: 'mono', text: `${p.field}:${p.term}` })),
            el('p', { text: `${fmt.numero(p.docFreq)} documento(s) · ${fmt.numero(p.totalTermFreq)} ocorrência(s) · idf ${fmt.decimal(p.idf, 3)}` }))),
        tabela(
          ['Segmento', { texto: 'df', num: true }, 'Blocos de 128', { texto: 'Ponteiro .doc', num: true }, { texto: 'Ponteiro .pos', num: true }, { texto: 'Freq. máx.', num: true }],
          p.segments.map((s) => el('tr', {},
            el('td', { class: 'mono', text: s.segment }),
            el('td', { class: 'num', text: fmt.numero(s.docFreq) }),
            el('td', { class: 'mudo', text: s.singleton ? 'documento único no dicionário' : s.skipBlocks > 0 ? `${fmt.numero(s.skipBlocks)} com tabela de saltos` : 'cauda em VInt' }),
            el('td', { class: 'num mono', text: fmt.numero(s.docPointer) }),
            el('td', { class: 'num mono', text: fmt.numero(s.positionPointer) }),
            el('td', { class: 'num', text: fmt.numero(s.maxFreq) })))),
        el('div', { style: { borderTop: '1px solid var(--linha)' } },
          tabela(
            ['Documento', { texto: 'Freq.', num: true }, 'Posições'],
            p.postings.map((d) => el('tr', {},
              el('td', {}, el('div', { text: d.title }), el('div', { class: 'mono mudo', style: { fontSize: '0.75rem' }, text: d.key })),
              el('td', { class: 'num', text: fmt.numero(d.freq) }),
              el('td', { class: 'posicoes', text: d.positions.length ? d.positions.slice(0, 16).join(', ') + (d.positions.length > 16 ? ' …' : '') : '—' }))))));
    } catch (erro) {
      detalhe.replaceChildren(el('div', { class: 'vazio-painel', text: erro.message }));
    }
  }

  const textoAnalise = el('input', { type: 'text', value: 'Os motores de busca indexaram milhares de páginas rapidamente', 'aria-label': 'Texto para analisar' });
  const passos = el('div', { class: 'passos' });
  const idiomaAnalise = el('p', { class: 'mudo', style: { marginTop: '10px' } });
  async function analisar() {
    const r = await api(`/analise${parametros({ texto: textoAnalise.value })}`);
    idiomaAnalise.textContent = `Idioma detectado: ${r.language === 'en' ? 'inglês (Porter)' : 'português (RSLP)'}. Posições contam também as palavras descartadas, para as frases continuarem exatas.`;
    passos.replaceChildren(...r.tokens.map((t) => el('div', { class: `passo ${t.indexed ? '' : 'descartado'}` },
      el('span', { class: 'original', text: `${t.position} · ${t.token}` }),
      el('span', { class: 'termo', text: t.indexed ?? t.lowercase }),
      el('small', { text: t.indexed ? `pt ${t.portuguese ?? '—'} · en ${t.english ?? '—'}` : 'stopword descartada' }))));
  }

  alvo.append(
    cabecalho('Índice', 'Termos', 'O dicionário do índice invertido e o caminho de cada palavra até virar termo.'),
    el('div', { class: 'termos-grade' },
      el('section', { class: 'painel' },
        el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Dicionário' }), el('p', { text: 'Termos somados entre todos os segmentos.' }))),
        el('div', { class: 'filtros-termos' }, campo, prefixo, ordemArea),
        lista),
      detalhe),
    el('section', { class: 'painel', style: { marginTop: '16px' } },
      el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Analisador' }), el('p', { text: 'Tokenização, minúsculas, stopwords, stemming e remoção de acentos, na mesma ordem usada na indexação.' }))),
      el('div', { class: 'painel-corpo' },
        el('form', { class: 'analise-entrada', onsubmit: (e) => { e.preventDefault(); analisar(); } },
          textoAnalise,
          el('button', { class: 'botao secundario', type: 'submit', text: 'Analisar' })),
        idiomaAnalise,
        el('div', { style: { marginTop: '12px' } }, passos))));

  desenharOrdem();
  await Promise.all([carregarTermos(), analisar()]);
  return null;
}
