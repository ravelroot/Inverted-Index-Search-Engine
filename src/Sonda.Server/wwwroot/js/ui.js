const ICONES = {
  busca: '<circle cx="11" cy="11" r="6.5"/><path d="m16 16 4.5 4.5"/>',
  indice: '<rect x="3.5" y="4" width="17" height="4" rx="1"/><rect x="3.5" y="10" width="17" height="4" rx="1"/><rect x="3.5" y="16" width="17" height="4" rx="1"/>',
  pasta: '<path d="M3.5 7a2 2 0 0 1 2-2h4l2 2.5h7a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2Z"/>',
  termos: '<path d="M5 6h14M5 12h9M5 18h5"/><circle cx="17.5" cy="16.5" r="2.5"/>',
  lua: '<path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5Z"/>',
  sol: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/>',
  api: '<path d="m8 8-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14"/>',
  fechar: '<path d="M6 6l12 12M18 6 6 18"/>',
  atualizar: '<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3"/><path d="M19.5 4.5v4h-4"/>',
  mais: '<path d="M12 5v14M5 12h14"/>',
  escudo: '<path d="M12 3.5 5 6v5.5c0 4.2 2.9 7.6 7 9 4.1-1.4 7-4.8 7-9V6Z"/><path d="m9 12 2 2 4-4"/>',
  mesclar: '<path d="M6 4v5a4 4 0 0 0 4 4h8M6 20v-5M15 9l4 4-4 4"/>',
};

export function icone(nome) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.7');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = ICONES[nome] ?? '';
  return svg;
}

export function el(tag, atributos = {}, ...filhos) {
  const elemento = document.createElement(tag);
  for (const [nome, valor] of Object.entries(atributos ?? {})) {
    if (valor === null || valor === undefined || valor === false) {
      continue;
    }
    if (nome === 'class') {
      elemento.className = valor;
    } else if (nome === 'text') {
      elemento.textContent = valor;
    } else if (nome.startsWith('on') && typeof valor === 'function') {
      elemento.addEventListener(nome.slice(2), valor);
    } else if (nome === 'dataset') {
      Object.assign(elemento.dataset, valor);
    } else if (nome === 'style' && typeof valor === 'object') {
      Object.assign(elemento.style, valor);
    } else if (valor === true) {
      elemento.setAttribute(nome, '');
    } else {
      elemento.setAttribute(nome, String(valor));
    }
  }
  anexar(elemento, filhos);
  return elemento;
}

function anexar(pai, filhos) {
  for (const filho of filhos) {
    if (filho === null || filho === undefined || filho === false) {
      continue;
    }
    if (Array.isArray(filho)) {
      anexar(pai, filho);
    } else if (filho instanceof Node) {
      pai.append(filho);
    } else {
      pai.append(document.createTextNode(String(filho)));
    }
  }
}

const numero = new Intl.NumberFormat('pt-BR');
const decimal = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2, minimumFractionDigits: 0 });
const dataHora = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
const data = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'medium' });

export const fmt = {
  numero: (valor) => numero.format(valor ?? 0),
  decimal: (valor, casas = 2) => new Intl.NumberFormat('pt-BR', { maximumFractionDigits: casas, minimumFractionDigits: casas }).format(valor ?? 0),
  curto: (valor) => decimal.format(valor ?? 0),
  bytes: (valor) => {
    const unidades = ['B', 'KB', 'MB', 'GB', 'TB'];
    let atual = valor ?? 0;
    let indice = 0;
    while (atual >= 1024 && indice < unidades.length - 1) {
      atual /= 1024;
      indice += 1;
    }
    return indice === 0 ? `${atual} B` : `${new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 }).format(atual)} ${unidades[indice]}`;
  },
  dataHora: (valor) => (valor ? dataHora.format(new Date(valor)) : '—'),
  data: (valor) => (valor ? data.format(new Date(valor)) : '—'),
  duracao: (segundos) => {
    if (segundos < 1) {
      return `${Math.round(segundos * 1000)} ms`;
    }
    if (segundos < 60) {
      return `${new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 }).format(segundos)} s`;
    }
    return `${Math.floor(segundos / 60)} min ${Math.round(segundos % 60)} s`;
  },
};

export function fragmentos(lista) {
  const resultado = document.createDocumentFragment();
  for (const parte of lista ?? []) {
    resultado.append(parte.match ? el('mark', { text: parte.text }) : document.createTextNode(parte.text));
  }
  return resultado;
}

export function arvoreExplicacao(explicacao) {
  const montar = (no) => el('li', {},
    el('span', { class: 'valor', text: new Intl.NumberFormat('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(no.value) }),
    ' ',
    el('span', { text: no.description }),
    no.details?.length ? el('ul', {}, no.details.map(montar)) : null);
  return el('div', { class: 'explicacao' }, el('ul', {}, montar(explicacao)));
}

export function aviso(mensagem, tipo = 'info') {
  const area = document.getElementById('avisos');
  const toast = el('div', { class: `toast ${tipo === 'erro' ? 'erro' : ''}`, role: 'status', text: mensagem });
  area.append(toast);
  setTimeout(() => toast.remove(), tipo === 'erro' ? 6000 : 3500);
}

export function segmentado(opcoes, valorAtual, aoMudar, rotulo) {
  return el('span', {},
    rotulo ? el('span', { class: 'opcao-rotulo', text: rotulo }) : null,
    el('span', { class: 'segmentado', role: 'group', 'aria-label': rotulo ?? '' },
      opcoes.map(([valor, texto]) => el('button', {
        type: 'button',
        'aria-pressed': String(valor === valorAtual),
        text: texto,
        onclick: () => aoMudar(valor),
      }))));
}

export function tabela(cabecalhos, linhas, opcoes = {}) {
  return el('div', { class: 'tabela-rolagem' },
    el('table', {},
      el('thead', {}, el('tr', {}, cabecalhos.map((c) => el('th', { class: c.num ? 'num' : null, text: c.texto ?? c })))),
      el('tbody', {}, linhas.length === 0
        ? el('tr', {}, el('td', { colspan: cabecalhos.length, class: 'mudo', text: opcoes.vazio ?? 'Nada para mostrar.' }))
        : linhas)));
}

export function ponto(classe) {
  return el('span', { class: `ponto ${classe ?? ''}` });
}

export function base64Url(texto) {
  const bytes = new TextEncoder().encode(texto);
  let binario = '';
  bytes.forEach((b) => { binario += String.fromCharCode(b); });
  return btoa(binario).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

export function preencher(alvo, ...filhos) {
  const lista = [];
  const juntar = (itens) => itens.forEach((item) => {
    if (Array.isArray(item)) {
      juntar(item);
    } else if (item !== null && item !== undefined && item !== false) {
      lista.push(item);
    }
  });
  juntar(filhos);
  alvo.replaceChildren(...lista);
  return alvo;
}

export function carregando(texto = 'Carregando…') {
  return el('div', { class: 'carregando', text: texto });
}
