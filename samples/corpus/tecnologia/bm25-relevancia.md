# Relevância com BM25

Encontrar os documentos que contêm as palavras da consulta é só metade do trabalho. A outra metade é ordenar esses documentos pela relevância. O modelo BM25, sigla de Best Matching 25, é o padrão usado por Lucene, Elasticsearch e OpenSearch.

## Os três ingredientes

1. **Frequência do termo (tf):** quanto mais vezes a palavra aparece no documento, maior a pontuação, mas com saturação. A décima ocorrência vale bem menos que a primeira. O parâmetro k1, normalmente 1,2, controla essa saturação.
2. **Frequência inversa de documentos (idf):** palavras raras valem mais do que palavras comuns. Um termo presente em quase todos os documentos quase não ajuda a separar o que é relevante.
3. **Normalização pelo tamanho:** um documento longo tem mais chances de conter a palavra por acaso. O parâmetro b, normalmente 0,75, define o quanto o tamanho do campo, comparado à média, reduz a pontuação.

A fórmula combina os três: pontuação = idf × tf × (k1 + 1) / (tf + k1 × (1 − b + b × tamanho / tamanho médio)).

## BM25 contra TF-IDF clássico

O TF-IDF clássico multiplica a raiz da frequência pelo quadrado do idf e divide pela raiz do tamanho do campo. Ele não tem saturação: um documento que repete uma palavra cem vezes ganha muito mais pontos do que deveria. Por isso o BM25 costuma produzir rankings melhores, especialmente em coleções com documentos de tamanhos muito diferentes.

## Pesos por campo

É comum dar mais peso ao título do que ao corpo. Uma palavra no título costuma descrever o assunto principal do documento, então o título recebe peso 2 enquanto o corpo recebe peso 1.
