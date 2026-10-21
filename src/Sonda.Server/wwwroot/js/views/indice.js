import { api } from '../api.js';
import { atualizarSituacao } from '../app.js';
import { aviso, carregando, el, fmt, icone, ponto, tabela } from '../ui.js';

const NOMES_CAMPO = { title: 'Título', body: 'Corpo', path: 'Caminho', id: 'Chave do documento' };
const ORIGEM = { flush: 'buffer', merge: 'mesclagem' };
const NOMES_IDIOMA = { pt: 'Português', en: 'Inglês' };

export async function render(alvo) {
  alvo.append(
    el('header', { class: 'cabecalho' },
      el('div', {}, el('h1', { text: 'Visão geral do índice' }), el('p', { text: 'Segmentos imutáveis em disco, estatísticas dos campos e manutenção.' }))),
    carregando());

  const atualizar = async () => {
    try {
      const dados = await api('/indice');
      alvo.replaceChildren(alvo.firstChild, ...montar(dados, atualizar));
    } catch (erro) {
      alvo.replaceChildren(alvo.firstChild, el('div', { class: 'aviso-relaxado', text: erro.message }));
    }
  };

  await atualizar();
  const aoMudar = () => atualizar();
  window.addEventListener('sonda:indice', aoMudar);
  return () => window.removeEventListener('sonda:indice', aoMudar);
}

function montar(dados, atualizar) {
  const corpo = dados.fields.find((f) => f.field === 'body');
  const maiorSegmento = Math.max(1, ...dados.segments.map((s) => s.documents));
  const fichas = el('div', { class: 'fichas' },
    ficha('Documentos', fmt.numero(dados.documents), dados.deleted > 0 ? `${fmt.numero(dados.deleted)} excluídos aguardando mesclagem` : 'nenhum excluído pendente'),
    ficha('Termos distintos', fmt.numero(corpo?.terms ?? 0), 'no campo corpo, somando segmentos'),
    ficha('Segmentos', fmt.numero(dados.segments.length), `ponto de gravação segments_${dados.generation}`),
    ficha('Tamanho em disco', fmt.bytes(dados.sizeBytes), 'todos os arquivos do índice'),
    ficha('Última gravação', dados.lastCommit ? fmt.dataHora(dados.lastCommit) : 'nunca', dados.writer ? `${fmt.numero(dados.writer.bufferedDocuments)} documento(s) no buffer` : 'aberto só para leitura'));

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

  return [fichas, el('div', { class: 'duas-colunas' }, el('div', { class: 'pilha' }, segmentos, campos), el('div', { class: 'pilha' }, distribuicao, manutencao))];
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
    el('h3', { style: { fontSize: 'var(--t-xs)', color: 'var(--cinza)', marginBottom: '8px' }, text: titulo }),
    entradas.length === 0
      ? el('p', { class: 'mudo', text: '—' })
      : entradas.map(([valor, quantidade]) => el('div', { class: 'linha' },
        el('span', {}, comPonto ? ponto(valor) : null, nomes[valor] ?? valor),
        el('div', { class: 'barra' }, el('i', { style: { width: `${(quantidade / Math.max(1, total)) * 100}%` } })),
        el('span', { class: 'num', text: fmt.numero(quantidade) }))));
}
