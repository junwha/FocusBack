#ifndef PQ_STRINGS_H
#define PQ_STRINGS_H

typedef struct { const char* key; const char* en; const char* ko; } pq_string;

extern const pq_string PQ_STRINGS[];
extern const int PQ_STRING_COUNT;

#endif
