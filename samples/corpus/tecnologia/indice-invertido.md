# Índice invertido: a estrutura por trás de toda busca

Um índice invertido é a estrutura de dados que permite encontrar, em milissegundos, quais documentos contêm uma palavra. Em vez de percorrer cada arquivo procurando o texto, como faria um comando LIKE em um banco de dados relacional, o motor de busca consulta um dicionário de termos e lê apenas a lista de documentos associada a cada termo.

## Dicionário e listas de postagens

O índice tem duas partes. O **dicionário** guarda todos os termos distintos em ordem, junto com estatísticas como a frequência de documentos (df). Cada termo aponta para uma **lista de postagens**, que contém os identificadores dos documentos onde ele aparece, quantas vezes aparece em cada um e, opcionalmente, as posições de cada ocorrência.

As posições permitem consultas de frase: para encontrar "motor de busca" basta verificar se as posições de "motor" e "busca" estão separadas pela distância certa.

## Por que ordenar e comprimir

Os identificadores de documento são gravados em ordem crescente. Isso permite guardar apenas a diferença entre um identificador e o anterior, técnica chamada de codificação delta. Diferenças pequenas ocupam poucos bits, e por isso as listas são compactadas em blocos de 128 valores com empacotamento de bits.

Listas ordenadas também permitem intersecções rápidas. Para uma consulta com dois termos obrigatórios, o motor avança as duas listas ao mesmo tempo e usa tabelas de saltos para pular blocos inteiros que não podem conter documentos em comum.

## Segmentos imutáveis

Motores como o Lucene não alteram arquivos já gravados. Novos documentos ficam em memória e, quando o buffer enche, viram um novo segmento. Exclusões são marcadas num mapa de bits à parte. De tempos em tempos, segmentos pequenos são mesclados em um maior, o que remove de vez os documentos excluídos.
