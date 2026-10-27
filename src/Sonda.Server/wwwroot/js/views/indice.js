import { api } from '../api.js';
import { atualizarSituacao, navegar } from '../app.js';
import { aviso, cabecalho, carregando, el, fmt, icone, ponto, tabela } from '../ui.js';

const NOMES_CAMPO = { title: 'Título', body: 'Corpo', path: 'Caminho', id: 'Chave do documento' };
const ORIGEM = { flush: 'buffer', merge: 'mesclagem' };
const NOMES_IDIOMA = { pt: 'Português', en: 'Inglês' };

export async function render(alvo) {
  alvo.append(
    cabecalho('Índice', 'Visão geral', 'Segmentos imutáveis em disco, estatísticas dos campos, o que as pessoas estão procurando e a manutenção do índice.'),
    carregando());

  const atualizar = async () => {
    try {
      const [dados, consultas] = await Promise.all([api('/indice'), api('/buscas/estatisticas').catch(() => null)]);
      alvo.replaceChildren(alvo.firstChild, ...montar(dados, atualizar, consultas));
    } catch (erro) {
      alvo.replaceChildren(alvo.firstChild, el('div', { class: 'aviso-relaxado', text: erro.message }));
    }
  };

  await atualizar();
  const aoMudar = () => atualizar();
  window.addEventListener('sonda:indice', aoMudar);
  return () => window.removeEventListener('sonda:indice', aoMudar);
}

function montar(dados, atualizar, consultas) {
  const corpo = dados.fields.find((f) => f.field === 'body');
  const maiorSegmento = Math.max(1, ...dados.segments.map((s) => s.documents));
  const fichas = el('div', { class: 'fichas' },
    ficha('Documentos', fmt.numero(dados.documents), dados.deleted > 0 ? `${fmt.numero(dados.deleted)} excluídos aguardando mesclagem` : 'nenhum excluído pendente'),
    ficha('Termos distintos', fmt.numero(corpo?.terms ?? 0), 'no campo corpo, somando segmentos'),
    ficha('Segmentos', fmt.numero(dados.segments.length), `ponto de gravação segments_${dados.generation}`),
    ficha('Tamanho em disco', fmt.bytes(dados.sizeBytes), 'todos os arquivos do índice'),
    ficha('Última gravação', dados.lastCommit ? new Date(dados.lastCommit).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' }) : 'nunca', `${dados.lastCommit ? `${fmt.data(dados.lastCommit)} · ` : ''}${dados.writer ? `${fmt.numero(dados.writer.bufferedDocuments)} no buffer` : 'só leitura'}`));

  const segmentos = el('section', { class: 'painel' },
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Segmentos' }), el('p', { text: 'Cada segmento tem dicionário, postagens, posições, normas, campos armazenados e doc values próprios.' }))),
    tabela(
      ['Segmento', 'Origem', { texto: 'Documentos', num: true }, 'Ocupação', { texto: 'Excluídos', num: true }, { texto: 'Tamanho', num: true }, 'Criado em'],
      dados.segments.map((s) => el('tr', {},
        el('td', { class: 'mono', text: s.name }),
        el('td', { class: 'mudo', text: ORIGEM[s.origin] ?? s.origin }),
        el('td', { class: 'num', text: fmt.numero(s.documents) }),
        el('td', {}, barra(s.documents / maiorSegmento, s.documents ? s.deleted / s.documents : 0)),
        el('td', { class: 'num', text: fmt.numero(s.deleted) }),
        el('td', { class: 'num', text: fmt.bytes(s.sizeBytes) }),
        el('td', { class: 'mudo', text: fmt.dataHora(s.createdAt) }))),
      { vazio: 'O índice ainda está vazio. Adicione uma fonte e rode a indexação.' }));

  const campos = el('section', { class: 'painel' },
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Campos' }), el('p', { text: 'O tamanho médio de cada campo entra na normalização do BM25.' }))),
    tabela(
      ['Campo', { texto: 'Termos', num: true }, { texto: 'Documentos', num: true }, { texto: 'Tokens', num: true }, { texto: 'Tamanho médio', num: true }],
      dados.fields.map((f) => el('tr', {},
        el('td', { text: NOMES_CAMPO[f.field] ?? f.field }),
        el('td', { class: 'num', text: fmt.numero(f.terms) }),
        el('td', { class: 'num', text: fmt.numero(f.documents) }),
        el('td', { class: 'num', text: fmt.numero(f.tokens) }),
        el('td', { class: 'num', text: fmt.decimal(f.averageLength, 1) })))));

  const resultadoManutencao = el('p', { class: 'mudo', style: { marginTop: '10px' } });
  const otimizar = el('button', { class: 'botao secundario', type: 'button' }, icone('mesclar'), 'Otimizar índice');
  const verificar = el('button', { class: 'botao secundario', type: 'button' }, icone('escudo'), 'Verificar integridade');
  otimizar.addEventListener('click', async () => {
    otimizar.disabled = true;
    resultadoManutencao.textContent = 'Mesclando segmentos…';
    try {
      const resposta = await api('/indice/otimizar?segmentos=1', { metodo: 'POST' });
      resultadoManutencao.textContent = `${resposta.mesclagens} mesclagem(ns). Agora são ${resposta.indice.segments.length} segmento(s) e ${fmt.bytes(resposta.indice.sizeBytes)}.`;
      aviso('Índice otimizado.');
      atualizarSituacao();
      atualizar();
    } catch (erro) {
      resultadoManutencao.textContent = erro.message;
      aviso(erro.message, 'erro');
    } finally {
      otimizar.disabled = false;
    }
  });
  verificar.addEventListener('click', async () => {
    verificar.disabled = true;
    try {
      const r = await api('/indice/verificar', { metodo: 'POST' });
      resultadoManutencao.textContent = r.problems.length === 0
        ? `${r.valid} de ${r.files} arquivos com checksum CRC32 válido (${fmt.decimal(r.elapsedMs, 0)} ms).`
        : `${r.problems.length} problema(s): ${r.problems.join('; ')}`;
    } catch (erro) {
      resultadoManutencao.textContent = erro.message;
    } finally {
      verificar.disabled = false;
    }
  });

  const manutencao = el('section', { class: 'painel' },
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Manutenção' }), el('p', { text: 'A mesclagem junta os segmentos e remove de vez os documentos excluídos.' }))),
    el('div', { class: 'painel-corpo' },
      el('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap' } }, otimizar, verificar),
      resultadoManutencao,
      dados.writer ? el('p', { class: 'mudo', style: { marginTop: '10px' }, text: `Buffer em memória: ${fmt.bytes(dados.writer.bufferedBytes)} · exclusões pendentes: ${fmt.numero(dados.writer.pendingDeletes)}${dados.writer.merging ? ' · mesclagem em andamento' : ''}` }) : null,
      el('p', { class: 'mudo mono', style: { marginTop: '6px', wordBreak: 'break-all' }, text: dados.path })));

  const distribuicao = el('section', { class: 'painel' },
    el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Distribuição' }), el('p', { text: 'Documentos vivos por fonte, idioma detectado e tipo de arquivo.' }))),
    el('div', { class: 'painel-corpo distribuicao' },
      grupo('Fonte', dados.bySource, dados.documents, false),
      grupo('Idioma', dados.byLanguage, dados.documents, true, NOMES_IDIOMA),
      grupo('Tipo', dados.byExtension, dados.documents, true)));

  return [fichas, el('div', { class: 'duas-colunas' }, el('div', { class: 'pilha' }, painelConsultas(consultas), segmentos, campos), el('div', { class: 'pilha' }, distribuicao, manutencao))];
}

function painelConsultas(dados) {
  const cabecalhoPainel = el('div', { class: 'painel-cabecalho' },
    el('div', {}, el('h2', { text: 'Consultas' }), el('p', { text: 'O que foi pesquisado desde que o servidor iniciou. Buscas sem resultado mostram lacunas no acervo.' })));
  if (!dados) {
    return el('section', { class: 'painel' }, cabecalhoPainel, el('div', { class: 'vazio-painel', text: 'Estatísticas de busca indisponíveis.' }));
  }
  const item = (c, detalhe) => el('button', { type: 'button', class: 'consulta-item', onclick: () => navegar('busca', { q: c.consulta }) }, el('span', { text: c.consulta }), el('small', { text: detalhe }));
  return el('section', { class: 'painel' },
    cabecalhoPainel,
    el('div', { class: 'consultas-grade' },
      el('div', {}, el('span', { text: 'Buscas' }), el('strong', { text: fmt.numero(dados.total) })),
      el('div', {}, el('span', { text: 'Sem resultado' }), el('strong', { text: dados.total ? `${fmt.decimal((dados.semResultado / dados.total) * 100, 0)}%` : '—' })),
      el('div', {}, el('span', { text: 'Tempo médio' }), el('strong', { text: dados.total ? `${fmt.decimal(dados.mediaMs, 1)} ms` : '—' })),
      el('div', {}, el('span', { text: 'p95' }), el('strong', { text: dados.total ? `${fmt.decimal(dados.p95Ms, 1)} ms` : '—' }))),
    el('div', { class: 'consultas-listas' },
      el('div', {}, el('h3', { text: 'Mais pesquisadas' }),
        dados.principais?.length ? dados.principais.slice(0, 6).map((c) => item(c, `${c.vezes}× · ${fmt.numero(c.ultimoTotal)} res.`)) : el('p', { class: 'mudo', style: { padding: '6px 18px 14px', fontSize: 'var(--t-sm)' }, text: 'Nenhuma busca ainda.' })),
      el('div', {}, el('h3', { text: 'Sem resultado' }),
        dados.semResultados?.length ? dados.semResultados.slice(0, 6).map((c) => item(c, `${c.vezes}×`)) : el('p', { class: 'mudo', style: { padding: '6px 18px 14px', fontSize: 'var(--t-sm)' }, text: 'Toda busca encontrou algo.' }))));
}

function ficha(rotulo, valor, detalhe) {
  return el('div', { class: 'ficha' }, el('span', { text: rotulo }), el('strong', { text: valor }), el('small', { text: detalhe }));
}

function barra(proporcao, excluidos) {
  const fundo = el('div', { class: 'barra', title: `${fmt.decimal(excluidos * 100, 1)}% excluídos` });
  fundo.append(el('i', { style: { width: `${Math.max(2, proporcao * 100)}%` } }));
  if (excluidos > 0) {
    fundo.append(el('i', { class: 'excluido', style: { left: `${proporcao * (1 - excluidos) * 100}%`, width: `${proporcao * excluidos * 100}%` } }));
  }
  return fundo;
}

function grupo(titulo, valores, total, comPonto, nomes = {}) {
  const entradas = Object.entries(valores ?? {});
  return el('div', {},
    el('h3', { text: titulo }),
    entradas.length === 0
      ? el('p', { class: 'mudo', text: '—' })
      : entradas.map(([valor, quantidade]) => el('div', { class: 'linha' },
        el('span', {}, comPonto ? ponto(valor) : null, nomes[valor] ?? valor),
        el('div', { class: 'barra' }, el('i', { style: { width: `${(quantidade / Math.max(1, total)) * 100}%` } })),
        el('span', { class: 'num', text: fmt.numero(quantidade) }))));
}
