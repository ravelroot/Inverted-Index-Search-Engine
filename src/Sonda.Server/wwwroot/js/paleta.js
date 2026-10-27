import { api, parametros } from './api.js';
import { alternarTema, atualizarSituacao, navegar, rotaAtual } from './app.js';
import { aviso, copiar, el, ehMac, historico, icone, salvas, tecla } from './ui.js';

function normalizar(texto) {
  return texto.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

function comandosFixos() {
  const { nome, parametros: atuais } = rotaAtual();
  const consulta = atuais.get('q') ?? '';
  const lista = [
    { grupo: 'Ir para', icone: 'busca', texto: 'Busca', detalhe: 'g b', acao: () => navegar('busca') },
    { grupo: 'Ir para', icone: 'indice', texto: 'Visão geral do índice', detalhe: 'g i', acao: () => navegar('indice') },
    { grupo: 'Ir para', icone: 'pasta', texto: 'Fontes e indexação', detalhe: 'g f', acao: () => navegar('fontes') },
    { grupo: 'Ir para', icone: 'termos', texto: 'Termos e analisador', detalhe: 'g t', acao: () => navegar('termos') },
    { grupo: 'Ir para', icone: 'tomada', texto: 'Integrações e API', detalhe: 'g n', acao: () => navegar('integracoes') },
    { grupo: 'Ações', icone: 'lua', texto: 'Alternar tema claro e escuro', acao: alternarTema },
    { grupo: 'Ações', icone: 'link', texto: 'Copiar link desta tela', acao: () => copiar(location.href, 'Link copiado.') },
    {
      grupo: 'Ações',
      icone: 'mesclar',
      texto: 'Otimizar índice (mesclar segmentos)',
      acao: async () => {
        try {
          const r = await api('/indice/otimizar?segmentos=1', { metodo: 'POST' });
          aviso(`${r.mesclagens} mesclagem(ns). Agora são ${r.indice.segments.length} segmento(s).`);
          atualizarSituacao();
        } catch (erro) {
          aviso(erro.message, 'erro');
        }
      },
    },
    {
      grupo: 'Ações',
      icone: 'escudo',
      texto: 'Verificar integridade (CRC32)',
      acao: async () => {
        try {
          const r = await api('/indice/verificar', { metodo: 'POST' });
          aviso(r.problems.length === 0 ? `${r.valid} de ${r.files} arquivos íntegros.` : `${r.problems.length} problema(s) encontrados.`, r.problems.length ? 'erro' : 'info');
        } catch (erro) {
          aviso(erro.message, 'erro');
        }
      },
    },
    { grupo: 'Ações', icone: 'teclado', texto: 'Mostrar atalhos de teclado', detalhe: '?', acao: abrirAtalhos },
  ];
  if (nome === 'busca' && consulta) {
    const filtros = Object.fromEntries([...atuais.entries()].filter(([chave]) => ['q', 'ordem', 'similaridade', 'operador', 'tipo', 'idioma', 'fonte'].includes(chave)));
    lista.splice(5, 0,
      { grupo: 'Esta busca', icone: 'tabela', texto: `Exportar “${consulta}” em CSV`, acao: () => { location.href = `api/busca/exportar${parametros({ ...filtros, formato: 'csv' })}`; } },
      { grupo: 'Esta busca', icone: 'documento', texto: `Exportar “${consulta}” em JSON`, acao: () => { location.href = `api/busca/exportar${parametros({ ...filtros, formato: 'json' })}`; } },
      { grupo: 'Esta busca', icone: 'feed', texto: 'Copiar endereço do feed Atom', acao: () => copiar(new URL(`api/busca/feed${parametros(filtros)}`, location.href).href, 'Endereço do feed copiado.') });
  }
  return lista;
}

let aberta = null;

export function abrirPaleta(textoInicial = '') {
  if (aberta) {
    aberta.querySelector('input').focus();
    return;
  }
  const anterior = document.activeElement;
  const entrada = el('input', { type: 'text', placeholder: 'Busque um comando, uma tela ou um documento…', 'aria-label': 'Comando', autocomplete: 'off', spellcheck: 'false', role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'paleta-lista' });
  entrada.value = textoInicial;
  const lista = el('div', { class: 'paleta-lista', id: 'paleta-lista', role: 'listbox' });
  let itens = [];
  let selecionado = 0;
  let documentos = [];
  let pedido = 0;

  const fechar = () => {
    fundo.remove();
    aberta = null;
    document.removeEventListener('keydown', teclas, true);
    if (anterior instanceof HTMLElement) {
      anterior.focus();
    }
  };

  const executar = (item) => {
    fechar();
    item.acao();
  };

  function desenhar() {
    const texto = entrada.value.trim();
    const chave = normalizar(texto);
    const fixos = comandosFixos().filter((c) => !chave || normalizar(`${c.grupo} ${c.texto}`).includes(chave));
    const recentes = [
      ...salvas().map((q) => ({ grupo: 'Buscas salvas', icone: 'estrela', texto: q, acao: () => navegar('busca', { q }) })),
      ...historico().filter((q) => !salvas().includes(q)).slice(0, 5).map((q) => ({ grupo: 'Buscas recentes', icone: 'relogio', texto: q, acao: () => navegar('busca', { q }) })),
    ].filter((c) => !chave || normalizar(c.texto).includes(chave));
    const docs = documentos.map((d) => ({
      grupo: 'Documentos',
      icone: 'documento',
      texto: d.titulo,
      detalhe: `${d.fonte}:${d.caminho}`,
      acao: () => navegar('busca', { q: texto, abrir: d.chave }),
    }));
    itens = [
      ...(texto ? [{ grupo: 'Buscar', icone: 'busca', texto: `Buscar “${texto}”`, detalhe: 'Enter', acao: () => navegar('busca', { q: texto }) }] : []),
      ...docs,
      ...recentes.slice(0, texto ? 4 : 8),
      ...fixos,
    ];
    selecionado = Math.min(selecionado, Math.max(0, itens.length - 1));
    let grupoAtual = null;
    const filhos = [];
    itens.forEach((item, indice) => {
      if (item.grupo !== grupoAtual) {
        grupoAtual = item.grupo;
        filhos.push(el('h3', { text: item.grupo }));
      }
      filhos.push(el('button', {
        type: 'button',
        class: 'paleta-item',
        role: 'option',
        id: `paleta-item-${indice}`,
        'aria-selected': String(indice === selecionado),
        onclick: () => executar(item),
        onmousemove: () => {
          if (selecionado !== indice) {
            selecionado = indice;
            marcar();
          }
        },
      }, icone(item.icone), el('span', { class: 'texto', text: item.texto }), item.detalhe ? el('span', { class: 'detalhe', text: item.detalhe }) : null));
    });
    if (itens.length === 0) {
      filhos.push(el('div', { class: 'vazio-painel', text: 'Nada encontrado.' }));
    }
    lista.replaceChildren(...filhos);
    marcar();
  }

  function marcar() {
    lista.querySelectorAll('.paleta-item').forEach((botao, indice) => botao.setAttribute('aria-selected', String(indice === selecionado)));
    const atual = lista.querySelector(`#paleta-item-${selecionado}`);
    entrada.setAttribute('aria-activedescendant', atual ? atual.id : '');
    atual?.scrollIntoView({ block: 'nearest' });
  }

  let espera = null;
  entrada.addEventListener('input', () => {
    selecionado = 0;
    desenhar();
    clearTimeout(espera);
    const texto = entrada.value.trim();
    if (texto.length < 2) {
      documentos = [];
      return;
    }
    espera = setTimeout(async () => {
      const numero = ++pedido;
      try {
        const r = await api(`/sugestoes${parametros({ q: texto, limite: 5 })}`);
        if (numero === pedido && aberta) {
          documentos = r.documentos ?? [];
          desenhar();
        }
      } catch {
        documentos = [];
      }
    }, 140);
  });

  function teclas(evento) {
    if (evento.key === 'Escape') {
      evento.preventDefault();
      evento.stopPropagation();
      fechar();
    } else if (evento.key === 'ArrowDown') {
      evento.preventDefault();
      selecionado = (selecionado + 1) % Math.max(1, itens.length);
      marcar();
    } else if (evento.key === 'ArrowUp') {
      evento.preventDefault();
      selecionado = (selecionado - 1 + itens.length) % Math.max(1, itens.length);
      marcar();
    } else if (evento.key === 'Enter' && itens[selecionado]) {
      evento.preventDefault();
      executar(itens[selecionado]);
    }
  }

  const fundo = el('div', { class: 'paleta-fundo', onclick: (e) => { if (e.target === fundo) { fechar(); } } },
    el('div', { class: 'paleta', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Paleta de comandos' },
      el('div', { class: 'paleta-entrada' }, icone('comando'), entrada),
      lista,
      el('div', { class: 'paleta-rodape' },
        el('span', {}, tecla('↑', '↓'), 'navegar'),
        el('span', {}, tecla('Enter'), 'executar'),
        el('span', {}, tecla('Esc'), 'fechar'),
        el('span', {}, tecla(ehMac() ? '⌘' : 'Ctrl', 'K'), 'abrir de qualquer tela'))));
  aberta = fundo;
  document.body.append(fundo);
  document.addEventListener('keydown', teclas, true);
  desenhar();
  entrada.focus();
  if (textoInicial) {
    entrada.dispatchEvent(new Event('input'));
  }
}

const ATALHOS = [
  ['Geral', [
    ['Paleta de comandos', [ehMac() ? '⌘' : 'Ctrl', 'K']],
    ['Focar a busca', ['/']],
    ['Mostrar atalhos', ['?']],
    ['Fechar painel ou diálogo', ['Esc']],
  ]],
  ['Navegação', [
    ['Busca', ['g', 'b']],
    ['Visão geral', ['g', 'i']],
    ['Fontes', ['g', 'f']],
    ['Termos', ['g', 't']],
    ['Integrações', ['g', 'n']],
  ]],
  ['Resultados', [
    ['Próximo resultado', ['j']],
    ['Resultado anterior', ['k']],
    ['Abrir o resultado', ['Enter']],
    ['Salvar ou remover a busca', ['s']],
  ]],
  ['Documento aberto', [
    ['Próxima ocorrência', ['n']],
    ['Ocorrência anterior', ['Shift', 'n']],
    ['Copiar caminho', ['c']],
  ]],
];

export function abrirAtalhos() {
  if (document.querySelector('.paleta-fundo')) {
    return;
  }
  const anterior = document.activeElement;
  const fechar = () => {
    fundo.remove();
    document.removeEventListener('keydown', teclas, true);
    if (anterior instanceof HTMLElement) {
      anterior.focus();
    }
  };
  const teclas = (evento) => {
    if (evento.key === 'Escape' || evento.key === '?') {
      evento.preventDefault();
      evento.stopPropagation();
      fechar();
    }
  };
  const botaoFechar = el('button', { class: 'icone-botao', type: 'button', 'aria-label': 'Fechar', onclick: fechar }, icone('fechar'));
  const fundo = el('div', { class: 'paleta-fundo', onclick: (e) => { if (e.target === fundo) { fechar(); } } },
    el('div', { class: 'paleta', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Atalhos de teclado' },
      el('div', { class: 'painel-cabecalho' }, el('div', {}, el('h2', { text: 'Atalhos de teclado' }), el('p', { text: 'Funcionam em qualquer tela, menos enquanto você digita num campo.' })), botaoFechar),
      el('div', { class: 'atalhos' },
        ATALHOS.map(([grupo, linhas]) => [
          el('h3', { text: grupo }),
          ...linhas.map(([texto, teclasAtalho]) => el('div', { class: 'atalho-linha' }, el('span', { text: texto }), el('span', {}, tecla(...teclasAtalho)))),
        ]))));
  document.body.append(fundo);
  document.addEventListener('keydown', teclas, true);
  botaoFechar.focus();
}

