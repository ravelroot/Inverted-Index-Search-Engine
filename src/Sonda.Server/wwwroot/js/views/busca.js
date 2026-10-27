import { api, parametros } from '../api.js';
import { navegar, estado } from '../app.js';
import {
  alternarSalva, arvoreExplicacao, aviso, base64Url, baixar, carregando, copiar, el, escala, fmt, fragmentos,
  historico, icone, local, menu, ponto, preencher, registrarBusca, salvas, segmentado, sondagem, tecla,
} from '../ui.js';

const LEGENDA = [
  ['motor busca', 'Todos os termos, com stemming: acha "motores de buscas".'],
  ['cenoura OR chocolate', 'Qualquer um dos termos (também OU).'],
  ['"índice invertido"', 'Frase exata, na mesma ordem.'],
  ['"motor relevancia"~4', 'Frase com até 4 posições de folga.'],
  ['busc*', 'Prefixo: busca, buscador, buscando…'],
  ['algoritimo~', 'Aproximada: tolera erros de digitação.'],
  ['bm25 -tfidf', 'Exclui documentos com o termo.'],
  ['titulo:farol', 'Só no título (também corpo: e caminho:).'],
  ['tipo:html idioma:en', 'Filtros por extensão, idioma e fonte.'],
  ['(feijoada OR moqueca) receita^2', 'Grupos e peso extra num termo.'],
];

const EXEMPLOS = ['índice invertido', 'relevância bm25', 'farol', '"inverted index"', 'buracos negros', 'bolo cenoura', 'stemm*', 'tipo:html'];

const ROTULOS_FACETA = { ext: 'Tipo', lang: 'Idioma', source: 'Fonte' };
const NOMES_IDIOMA = { pt: 'Português', en: 'Inglês' };
const PARAMETRO_FACETA = { ext: 'tipo', lang: 'idioma', source: 'fonte' };
const FILTROS = ['q', 'ordem', 'similaridade', 'operador', 'tipo', 'idioma', 'fonte'];

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
    abrir: parametrosRota.get('abrir') ?? '',
  };
}

function filtrosDe(atual) {
  const saida = {};
  for (const chave of FILTROS) {
    if (atual[chave] && !(chave === 'ordem' && atual[chave] === 'relevancia')) {
      saida[chave] = atual[chave];
    }
  }
  return saida;
}

export async function render(alvo, parametrosRota) {
  const atual = lerEstado(parametrosRota);
  const ir = (mudancas) => navegar('busca', { ...filtrosDe(atual), explicar: atual.explicar ? '1' : '', ...mudancas, pagina: mudancas.pagina ?? 1 });
  const limpezas = [];

  const entrada = el('input', {
    type: 'search',
    name: 'q',
    value: atual.q,
    placeholder: 'Pesquise palavras, "frases", prefixos* ou campos como titulo:farol',
    autocomplete: 'off',
    spellcheck: 'false',
    'aria-label': 'Consulta',
    role: 'combobox',
    'aria-autocomplete': 'list',
    'aria-expanded': 'false',
    'aria-controls': 'sugestoes',
  });
  const sugestoes = el('div', { class: 'sugestoes', id: 'sugestoes', role: 'listbox', hidden: true });
  const formulario = el('form', {
    class: 'busca-barra',
    role: 'search',
    onsubmit: (e) => {
      e.preventDefault();
      fecharSugestoes();
      ir({ q: entrada.value.trim() });
    },
  },
  el('label', { class: 'busca-entrada' }, icone('sonar'), entrada, el('span', { class: 'atalho' }, tecla('/'))),
  el('button', { class: 'botao', type: 'submit' }, 'Sondar', icone('seta')));

  limpezas.push(ligarSugestoes(entrada, sugestoes, ir));

  const densidade = local.ler('densidade', 'confortavel');
  const opcoes = el('div', { class: 'busca-opcoes' },
    segmentado([['relevancia', 'Relevância'], ['recentes', 'Recentes']], atual.ordem, (v) => ir({ ordem: v === 'relevancia' ? '' : v }), 'Ordem'),
    segmentado([['bm25', 'BM25'], ['tfidf', 'TF-IDF']], atual.similaridade || (estado.info?.similaridade === 'TF-IDF' ? 'tfidf' : 'bm25'), (v) => ir({ similaridade: v }), 'Modelo'),
    segmentado([['', 'Todos os termos'], ['ou', 'Qualquer termo']], atual.operador, (v) => ir({ operador: v }), 'Combinar'),
    el('label', { class: 'marcador' },
      el('input', { type: 'checkbox', checked: atual.explicar, onchange: (e) => ir({ explicar: e.target.checked ? '1' : '', pagina: atual.pagina }) }),
      'Explicar pontuação'));

  const total = estado.info ? `${fmt.numero(estado.info.documentos)} documentos indexados em ${fmt.numero(estado.info.segmentos)} segmento(s).` : 'Índice local.';
  alvo.append(
    el('header', { class: 'cabecalho' },
      el('div', {},
        el('span', { class: 'sobretitulo', text: 'Sondagem' }),
        el('h1', { text: atual.q ? 'Resultados da busca' : 'O que você procura?' }),
        atual.q ? null : el('p', { text: `${total} Ranking por relevância com análise de texto em português e inglês.` }))),
    el('section', { class: 'busca-cartucho' }, el('div', { style: { position: 'relative' } }, formulario, sugestoes), opcoes));

  const area = el('section');
  alvo.append(area);
  if (!atual.q) {
    entrada.focus();
    area.append(estadoVazio(ir, entrada));
    return () => limpezas.forEach((f) => f());
  }

  registrarBusca(atual.q);
  area.append(carregando('Sondando o índice…'));
  const controle = new AbortController();
  let navegacao = null;
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
    const montado = resultados(resposta, atual, ir, densidade);
    area.replaceChildren(...montado.blocos);
    navegacao = montado.navegacao;
    if (atual.abrir) {
      const alvoHit = resposta.hits.find((h) => h.key === atual.abrir);
      abrirDocumento(alvoHit ?? { key: atual.abrir, title: atual.abrir, titleFragments: [{ text: atual.abrir }], source: '', path: '', size: 0 }, atual.q);
    }
  } catch (erro) {
    if (erro.name !== 'AbortError') {
      area.replaceChildren(el('div', { class: 'aviso-relaxado' }, icone('pulso'), el('span', { text: erro.message })));
    }
  }

  const teclas = (evento) => {
    if (evento.key === 'Escape' && document.querySelector('.gaveta')) {
      document.querySelector('.gaveta-fundo')?.click();
      return;
    }
    const digitando = evento.target instanceof HTMLElement && ['INPUT', 'TEXTAREA', 'SELECT'].includes(evento.target.tagName);
    if (digitando || evento.ctrlKey || evento.metaKey || evento.altKey || document.querySelector('.gaveta, .paleta-fundo')) {
      return;
    }
    if (evento.key === 's') {
      evento.preventDefault();
      const salva = alternarSalva(atual.q);
      aviso(salva ? 'Busca salva. Ela aparece na tela inicial e na paleta de comandos.' : 'Busca removida das salvas.');
      document.querySelector('[data-salvar]')?.replaceWith(botaoSalvar(atual.q));
      return;
    }
    navegacao?.(evento);
  };
  document.addEventListener('keydown', teclas);
  return () => {
    controle.abort();
    limpezas.forEach((f) => f());
    document.removeEventListener('keydown', teclas);
    document.querySelectorAll('.gaveta, .gaveta-fundo').forEach((n) => n.remove());
  };
}

function ligarSugestoes(entrada, caixa, ir) {
  let itens = [];
  let selecionado = -1;
  let pedido = 0;
  let espera = null;

  const fechar = () => {
    caixa.hidden = true;
    entrada.setAttribute('aria-expanded', 'false');
    selecionado = -1;
  };
  fecharSugestoesAtual = fechar;

  const desenhar = (documentos) => {
    const texto = entrada.value.trim();
    const chave = texto.toLowerCase();
    const anteriores = [...salvas(), ...historico()]
      .filter((q, i, todos) => todos.indexOf(q) === i)
      .filter((q) => q.toLowerCase() !== chave && (!chave || q.toLowerCase().includes(chave)))
      .slice(0, texto ? 3 : 6);
    itens = [
      ...anteriores.map((q) => ({ tipo: 'consulta', q, salva: salvas().includes(q) })),
      ...documentos.map((d) => ({ tipo: 'documento', d })),
    ];
    if (itens.length === 0) {
      fechar();
      return;
    }
    const filhos = [];
    if (anteriores.length) {
      filhos.push(el('h3', { text: texto ? 'Buscas anteriores' : 'Buscas salvas e recentes' }));
    }
    itens.forEach((item, indice) => {
      if (item.tipo === 'documento' && (indice === 0 || itens[indice - 1].tipo !== 'documento')) {
        filhos.push(el('h3', { text: 'Ir direto ao documento' }));
      }
      filhos.push(el('button', {
        type: 'button',
        class: 'sugestao',
        role: 'option',
        id: `sugestao-${indice}`,
        'aria-selected': String(indice === selecionado),
        onmousedown: (e) => e.preventDefault(),
        onclick: () => escolher(item),
      },
      item.tipo === 'consulta' ? icone(item.salva ? 'estrela' : 'relogio') : icone('documento'),
      el('span', { class: 'texto' }, item.tipo === 'consulta' ? item.q : realcar(item.d.titulo, ultimoTermo(texto))),
      item.tipo === 'documento' ? el('span', { class: 'detalhe', text: `${item.d.fonte}:${item.d.caminho}` }) : null));
    });
    caixa.replaceChildren(...filhos);
    caixa.hidden = false;
    entrada.setAttribute('aria-expanded', 'true');
  };

  const escolher = (item) => {
    fechar();
    if (item.tipo === 'consulta') {
      entrada.value = item.q;
      ir({ q: item.q });
    } else {
      ir({ q: entrada.value.trim(), abrir: item.d.chave });
    }
  };

  const marcar = () => {
    caixa.querySelectorAll('.sugestao').forEach((b, i) => b.setAttribute('aria-selected', String(i === selecionado)));
    entrada.setAttribute('aria-activedescendant', selecionado >= 0 ? `sugestao-${selecionado}` : '');
    caixa.querySelector(`#sugestao-${selecionado}`)?.scrollIntoView({ block: 'nearest' });
  };

  const buscarSugestoes = () => {
    clearTimeout(espera);
    const texto = entrada.value.trim();
    if (texto.length < 2) {
      desenhar([]);
      return;
    }
    espera = setTimeout(async () => {
      const numero = ++pedido;
      try {
        const r = await api(`/sugestoes${parametros({ q: texto, limite: 6 })}`);
        if (numero === pedido && document.activeElement === entrada) {
          desenhar(r.documentos ?? []);
        }
      } catch {
        desenhar([]);
      }
    }, 120);
  };

  const aoDigitar = () => {
    selecionado = -1;
    buscarSugestoes();
  };
  const aoFocar = () => {
    if (!entrada.value.trim()) {
      desenhar([]);
    }
  };
  const aoTeclar = (evento) => {
    if (caixa.hidden) {
      if (evento.key === 'ArrowDown') {
        buscarSugestoes();
      }
      return;
    }
    if (evento.key === 'ArrowDown') {
      evento.preventDefault();
      selecionado = Math.min(itens.length - 1, selecionado + 1);
      marcar();
    } else if (evento.key === 'ArrowUp') {
      evento.preventDefault();
      selecionado = Math.max(-1, selecionado - 1);
      marcar();
    } else if (evento.key === 'Enter' && selecionado >= 0) {
      evento.preventDefault();
      escolher(itens[selecionado]);
    } else if (evento.key === 'Escape') {
      evento.stopPropagation();
      fechar();
    }
  };
  entrada.addEventListener('input', aoDigitar);
  entrada.addEventListener('focus', aoFocar);
  entrada.addEventListener('keydown', aoTeclar);
  entrada.addEventListener('blur', () => setTimeout(fechar, 120));
  return () => clearTimeout(espera);
}

let fecharSugestoesAtual = () => {};

function fecharSugestoes() {
  fecharSugestoesAtual();
}

function ultimoTermo(texto) {
  const partes = texto.replace(/[^\p{L}\p{N}\s]/gu, ' ').trim().split(/\s+/);
  return partes[partes.length - 1] ?? '';
}

function semAcento(texto) {
  return texto.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

function realcar(titulo, termo) {
  if (!termo) {
    return titulo;
  }
  const base = semAcento(titulo);
  const procura = semAcento(termo);
  const inicio = base.indexOf(procura);
  if (inicio < 0 || base.length !== titulo.length) {
    return titulo;
  }
  return [titulo.slice(0, inicio), el('mark', { text: titulo.slice(inicio, inicio + termo.length) }), titulo.slice(inicio + termo.length)];
}

function estadoVazio(ir, entrada) {
  const salvasAtuais = salvas();
  const recentes = historico().filter((q) => !salvasAtuais.includes(q)).slice(0, 6);
  const listaConsultas = (lista, iconeNome, vazio, removivel) => el('div', { class: 'lista-rapida' },
    lista.length === 0
      ? el('div', { class: 'vazio-lista', text: vazio })
      : lista.map((q) => el('div', { class: 'item' },
        icone(iconeNome),
        el('button', { type: 'button', class: 'mono', text: q, onclick: () => ir({ q }) }),
        removivel ? el('button', {
          type: 'button',
          class: 'mini',
          title: 'Remover das salvas',
          'aria-label': `Remover ${q} das salvas`,
          onclick: (e) => {
            alternarSalva(q);
            e.currentTarget.closest('.item').remove();
          },
        }, icone('fechar')) : null)));

  return el('div', { class: 'vazio' },
    el('div', { class: 'painel' },
      el('div', { class: 'painel-cabecalho' },
        el('div', {}, el('h2', { text: 'Convenções de busca' }), el('p', { text: 'Sintaxe no estilo Lucene, com operadores também em português (E, OU, NAO). Clique para experimentar.' }))),
      el('div', { class: 'legenda' }, LEGENDA.map(([codigo, texto]) => el('button', {
        class: 'dica',
        type: 'button',
        onclick: () => {
          entrada.value = codigo;
          ir({ q: codigo });
        },
      }, el('code', { text: codigo }), el('span', { text: texto }))))),
    el('div', { class: 'pilha' },
      el('section', { class: 'painel' },
        el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Buscas salvas' }), el('p', {}, 'Salve uma busca com ', el('kbd', { text: 's' }), ' ou com a estrela nos resultados.'))),
        listaConsultas(salvasAtuais, 'estrela', 'Nenhuma busca salva ainda.', true)),
      el('section', { class: 'painel' },
        el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Recentes' }), el('p', { text: 'Ficam só neste navegador.' }))),
        listaConsultas(recentes, 'relogio', 'As suas últimas buscas aparecem aqui.', false)),
      el('section', { class: 'painel' },
        el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Experimente' }))),
        el('div', { class: 'exemplos' }, EXEMPLOS.map((exemplo) => el('button', { type: 'button', text: exemplo, onclick: () => ir({ q: exemplo }) }))))));
}

function botaoSalvar(consulta) {
  const salva = salvas().includes(consulta);
  return el('button', {
    class: 'botao secundario pequeno',
    type: 'button',
    'data-salvar': '',
    'aria-pressed': String(salva),
    title: 'Salvar busca (s)',
    onclick: (e) => {
      const agora = alternarSalva(consulta);
      aviso(agora ? 'Busca salva.' : 'Busca removida das salvas.');
      e.currentTarget.replaceWith(botaoSalvar(consulta));
    },
  }, icone('estrela'), salva ? 'Salva' : 'Salvar');
}

function resultados(resposta, atual, ir, densidade) {
  const totalTexto = resposta.totalIsLowerBound ? `${fmt.numero(resposta.total)}+` : fmt.numero(resposta.total);
  const filtros = filtrosDe(atual);
  const exportar = (formato) => {
    baixar(`api/busca/exportar${parametros({ ...filtros, formato, limite: 1000 })}`);
    aviso(`Exportando até 1.000 resultados em ${formato.toUpperCase()}.`);
  };
  const lista = el('div', { class: `resultados ${densidade === 'compacta' ? 'compacto' : ''}` });

  const botaoDensidade = el('button', {
    class: 'botao secundario pequeno',
    type: 'button',
    title: 'Alternar densidade da lista',
    onclick: (e) => {
      const compacta = lista.classList.toggle('compacto');
      local.gravar('densidade', compacta ? 'compacta' : 'confortavel');
      e.currentTarget.lastChild.textContent = compacta ? 'Compacta' : 'Confortável';
    },
  }, icone('densidade'), densidade === 'compacta' ? 'Compacta' : 'Confortável');

  const blocos = [
    el('div', { class: 'resumo-linha' },
      el('div', { class: 'resumo' },
        el('strong', { text: `${totalTexto} resultado${resposta.total === 1 ? '' : 's'}` }),
        el('span', { class: 'mudo', text: `${fmt.decimal(resposta.elapsedMs, 1)} ms · ${resposta.similarity}${atual.ordem === 'recentes' ? ' · mais recentes primeiro' : ''}` })),
      el('div', { class: 'ferramentas' },
        botaoSalvar(atual.q),
        botaoDensidade,
        el('button', { class: 'botao secundario pequeno', type: 'button', title: 'Copiar link desta busca', onclick: () => copiar(location.href, 'Link da busca copiado.') }, icone('link'), 'Link'),
        menu(el('button', { class: 'botao secundario pequeno', type: 'button' }, icone('baixar'), 'Exportar'), [
          { icone: 'tabela', texto: 'Planilha CSV', detalhe: '.csv', acao: () => exportar('csv') },
          { icone: 'documento', texto: 'JSON', detalhe: '.json', acao: () => exportar('json') },
          { icone: 'documento', texto: 'JSON por linha', detalhe: '.ndjson', acao: () => exportar('ndjson') },
          { icone: 'feed', texto: 'Copiar endereço do feed Atom', acao: () => copiar(new URL(`api/busca/feed${parametros(filtros)}`, location.href).href, 'Endereço do feed copiado. Cole no seu leitor de feeds.') },
          { icone: 'tomada', texto: 'Ver na tela Integrações', acao: () => navegar('integracoes', { q: atual.q }) },
        ]))),
    el('div', { class: 'consulta-interpretada mono', title: 'Consulta depois da análise de texto' }, el('span', { text: 'Consulta analisada' }), el('code', { text: resposta.parsedQuery })),
  ];
  if (resposta.relaxed) {
    blocos.push(el('div', { class: 'aviso-relaxado' }, icone('pulso'), el('span', { text: 'Nenhum documento tinha todos os termos, então a busca foi ampliada para qualquer um deles.' })));
  }

  if (resposta.hits.length === 0) {
    lista.append(el('div', { class: 'vazio-painel' },
      el('p', { text: 'Nenhum documento encontrado.' }),
      el('p', { class: 'mudo', style: { marginTop: '6px' }, text: 'Tente menos palavras, um prefixo (busc*), uma busca aproximada (palavra~) ou remova os filtros.' })));
  }

  const posicaoInicial = (resposta.page - 1) * resposta.pageSize;
  const maior = resposta.hits.reduce((m, h) => Math.max(m, h.score), 0) || 1;
  resposta.hits.forEach((hit, indice) => lista.append(cartao(hit, posicaoInicial + indice + 1, atual, hit.score / maior)));

  const paginas = Math.ceil(Math.min(resposta.total, 1000) / resposta.pageSize);
  if (paginas > 1) {
    lista.append(el('nav', { class: 'paginacao', 'aria-label': 'Páginas' },
      el('button', { class: 'botao secundario pequeno', type: 'button', disabled: resposta.page <= 1, onclick: () => ir({ pagina: resposta.page - 1 }) }, 'Anterior'),
      el('span', { class: 'mono', text: `Página ${resposta.page} de ${fmt.numero(paginas)}` }),
      el('button', { class: 'botao secundario pequeno', type: 'button', disabled: resposta.page >= paginas, onclick: () => ir({ pagina: resposta.page + 1 }) }, 'Próxima')));
  }

  blocos.push(el('div', { class: 'grade-busca' }, facetas(resposta.facets, atual, ir), lista));

  let ativo = -1;
  const navegacao = (evento) => {
    const cartoes = [...lista.querySelectorAll('.resultado')];
    if (cartoes.length === 0) {
      return;
    }
    if (evento.key === 'j' || evento.key === 'k') {
      evento.preventDefault();
      ativo = evento.key === 'j' ? Math.min(cartoes.length - 1, ativo + 1) : Math.max(0, ativo - 1);
      cartoes.forEach((c, i) => c.classList.toggle('ativo', i === ativo));
      cartoes[ativo].scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      cartoes[ativo].focus({ preventScroll: true });
    } else if ((evento.key === 'Enter' || evento.key === 'o') && ativo >= 0) {
      evento.preventDefault();
      cartoes[ativo].querySelector('h2 button')?.click();
    }
  };
  return { blocos, navegacao };
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
        title: selecionado === valor ? 'Remover filtro' : `Filtrar por ${valor}`,
        onclick: () => ir({ [parametro]: selecionado === valor ? '' : valor }),
      },
      el('span', { class: 'rotulo' }, campo === 'source' ? null : ponto(valor), campo === 'lang' ? (NOMES_IDIOMA[valor] ?? valor) : valor),
      el('span', { class: 'contagem', text: fmt.numero(contagem) }))));
  });
  return el('aside', { class: 'facetas', 'aria-label': 'Filtros' }, grupos);
}

function caminho(hit) {
  const partes = (hit.path ?? '').split('/');
  return el('div', { class: 'caminho' },
    el('span', { class: 'fonte', text: hit.source }),
    partes.map((parte, i) => [el('span', { class: 'sep', text: i === 0 ? ':' : '/' }), el('span', { text: parte })]));
}

function cartao(hit, posicao, atual, proporcao) {
  const explicacao = hit.explanation ? arvoreExplicacao(hit.explanation) : null;
  return el('article', { class: 'resultado', tabindex: '-1' },
    el('span', { class: 'posicao', text: String(posicao).padStart(2, '0') }),
    el('div', { class: 'corpo' },
      el('h2', {}, el('button', { type: 'button', onclick: () => abrirDocumento(hit, atual.q) }, fragmentos(hit.titleFragments))),
      caminho(hit),
      hit.snippet?.length ? el('p', { class: 'trecho' }, fragmentos(hit.snippet)) : null,
      el('div', { class: 'meta' },
        el('span', { class: 'etiqueta' }, ponto(hit.extension), hit.extension || 'sem extensão'),
        el('span', { class: 'etiqueta' }, ponto(hit.language), NOMES_IDIOMA[hit.language] ?? hit.language),
        el('span', { text: fmt.bytes(hit.size) }),
        el('span', { text: `modificado em ${fmt.data(hit.modified)}` }))),
    el('div', { class: 'sondagem', title: 'Pontuação de relevância (escala relativa ao primeiro resultado)' },
      sondagem(hit.score),
      escala(proporcao),
      el('small', { text: `${Math.round(proporcao * 100)}% do topo` })),
    explicacao);
}

async function abrirDocumento(hit, consulta) {
  document.querySelectorAll('.gaveta, .gaveta-fundo').forEach((n) => n.remove());
  const anterior = document.activeElement;
  const texto = el('div', { class: 'gaveta-corpo' }, carregando());
  const contador = el('span', { class: 'ocorrencias' }, 'Carregando…');
  const semelhantes = el('section', { class: 'semelhantes', hidden: true });
  let marcas = [];
  let atualMarca = -1;
  const chave = base64Url(hit.key);

  const irPara = (indice) => {
    if (marcas.length === 0) {
      return;
    }
    marcas[atualMarca]?.classList.remove('atual');
    atualMarca = (indice + marcas.length) % marcas.length;
    marcas[atualMarca].classList.add('atual');
    marcas[atualMarca].scrollIntoView({ block: 'center', behavior: 'smooth' });
    contador.replaceChildren(el('b', { text: fmt.numero(atualMarca + 1) }), ` de ${fmt.numero(marcas.length)} ocorrência${marcas.length === 1 ? '' : 's'}`);
  };

  const fechar = () => {
    fundo.remove();
    gaveta.remove();
    document.removeEventListener('keydown', teclas, true);
    if (anterior instanceof HTMLElement) {
      anterior.focus();
    }
  };
  const copiarCaminho = () => copiar(`${hit.source}:${hit.path}`, 'Caminho copiado.');
  const teclas = (evento) => {
    if (evento.key === 'Escape') {
      evento.preventDefault();
      evento.stopPropagation();
      fechar();
      return;
    }
    if (evento.ctrlKey || evento.metaKey || evento.altKey || (evento.target instanceof HTMLElement && ['INPUT', 'TEXTAREA'].includes(evento.target.tagName))) {
      return;
    }
    if (evento.key === 'n' || evento.key === 'N') {
      evento.preventDefault();
      evento.stopPropagation();
      irPara(atualMarca + (evento.shiftKey ? -1 : 1));
    } else if (evento.key === 'c') {
      evento.preventDefault();
      evento.stopPropagation();
      copiarCaminho();
    } else if (evento.key === 'j' || evento.key === 'k' || evento.key === 's') {
      evento.stopPropagation();
    }
  };

  const fundo = el('div', { class: 'gaveta-fundo', onclick: fechar });
  const titulo = el('h2', {}, fragmentos(hit.titleFragments));
  const linhaCaminho = hit.source ? caminho(hit) : el('div', { class: 'caminho' });
  const meta = el('div', { class: 'meta' }, hit.size ? el('span', { text: fmt.bytes(hit.size) }) : null, hit.modified ? el('span', { text: fmt.dataHora(hit.modified) }) : null);
  const gaveta = el('aside', { class: 'gaveta', role: 'dialog', 'aria-modal': 'true', 'aria-label': hit.title },
    el('div', { class: 'gaveta-topo' },
      el('div', {}, titulo, linhaCaminho, meta),
      el('button', { class: 'icone-botao', type: 'button', 'aria-label': 'Fechar (Esc)', title: 'Fechar (Esc)', onclick: fechar }, icone('fechar'))),
    el('div', { class: 'gaveta-barra' },
      contador,
      el('button', { class: 'mini', type: 'button', title: 'Ocorrência anterior (Shift+N)', 'aria-label': 'Ocorrência anterior', onclick: () => irPara(atualMarca - 1) }, icone('cima')),
      el('button', { class: 'mini', type: 'button', title: 'Próxima ocorrência (N)', 'aria-label': 'Próxima ocorrência', onclick: () => irPara(atualMarca + 1) }, icone('baixo')),
      el('button', { class: 'botao texto pequeno', type: 'button', title: 'Copiar caminho (C)', onclick: copiarCaminho }, icone('copiar'), 'Caminho'),
      el('button', { class: 'botao texto pequeno', type: 'button', title: 'Baixar o texto indexado', onclick: () => baixar(`api/documentos/${chave}/texto`) }, icone('baixar'), 'Texto')),
    texto,
    semelhantes);
  document.body.append(fundo, gaveta);
  document.addEventListener('keydown', teclas, true);
  gaveta.querySelector('.gaveta-topo button').focus();

  try {
    const visao = await api(`/documentos/${chave}${parametros({ q: consulta })}`);
    if (!hit.source && visao.document) {
      const d = visao.document;
      const completo = { ...hit, source: d.source, path: d.path, size: d.size, modified: d.modified, title: d.title };
      titulo.textContent = d.title;
      linhaCaminho.replaceWith(caminho(completo));
      meta.replaceChildren(el('span', { text: fmt.bytes(d.size) }), el('span', { text: fmt.dataHora(d.modified) }));
      hit = completo;
    }
    preencher(texto,
      visao.document.description ? el('p', { class: 'mudo', style: { marginBottom: '14px' }, text: visao.document.description }) : null,
      fragmentos(visao.body),
      visao.truncated ? el('p', { class: 'mudo', style: { marginTop: '14px' }, text: `Mostrando os primeiros 60 mil de ${fmt.numero(visao.length)} caracteres.` }) : null);
    marcas = [...texto.querySelectorAll('mark')];
    if (marcas.length) {
      irPara(0);
    } else {
      contador.textContent = consulta ? 'Nenhuma ocorrência no corpo' : 'Documento completo';
    }
  } catch (erro) {
    texto.replaceChildren(el('p', { class: 'mudo', text: erro.message }));
    contador.textContent = '';
  }

  try {
    const r = await api(`/documentos/${chave}/semelhantes?limite=5`);
    if (r.hits?.length) {
      semelhantes.hidden = false;
      preencher(semelhantes,
        el('h3', { text: 'Documentos semelhantes' }),
        r.termos?.length ? el('div', { class: 'termos-chave', title: 'Termos mais característicos deste documento' }, r.termos.slice(0, 10).map((t) => el('span', { text: t }))) : null,
        r.hits.map((h) => el('button', { type: 'button', class: 'semelhante', onclick: () => abrirDocumento(h, consulta) },
          el('strong', { text: h.title }),
          el('small', { text: `${h.source}:${h.path}` }))));
    }
  } catch {
    semelhantes.hidden = true;
  }
}
