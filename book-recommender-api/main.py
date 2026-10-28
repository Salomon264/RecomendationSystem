import numpy as np
import pandas as pd
from fastapi import FastAPI, HTTPException, Query
from scipy.sparse import csr_matrix
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.neighbors import NearestNeighbors

app = FastAPI(title="Book Recommender API")

books = pd.read_csv("Books.csv", low_memory=False)
ratings = pd.read_csv("Ratings.csv")

# убираем дубли названий, берём только явные оценки (0 = нет оценки)
books = books.drop_duplicates("Book-Title").dropna(subset=["Book-Title"])
books = books[["ISBN", "Book-Title", "Book-Author", "Publisher", "Image-URL-M"]]
df = ratings[ratings["Book-Rating"] > 0].merge(books[["ISBN", "Book-Title"]], on="ISBN")

# оставляем активных пользователей (>=10 оценок) и книги с >=5 оценками
df = df[df["User-ID"].map(df["User-ID"].value_counts()) >= 10]
df = df[df["Book-Title"].map(df["Book-Title"].value_counts()) >= 5]

users_idx = pd.Index(sorted(df["User-ID"].unique()))
titles_idx = pd.Index(sorted(df["Book-Title"].unique()))
rows = users_idx.get_indexer(df["User-ID"])
cols = titles_idx.get_indexer(df["Book-Title"])
shape = (len(users_idx), len(titles_idx))
M = csr_matrix((df["Book-Rating"].values.astype(float), (rows, cols)), shape=shape)  # пользователи x книги
# центрируем оценки по среднему пользователя (adjusted cosine) - убирает "завышающих" пользователей
centered = df["Book-Rating"] - df.groupby("User-ID")["Book-Rating"].transform("mean") + 1e-3
C = csr_matrix((centered.values, (rows, cols)), shape=shape)
CT = C.T.tocsr()  # книги x пользователи

info = books.set_index("Book-Title")[["Book-Author", "Image-URL-M"]]


def book_json(title, score=None):
    d = {"title": title,
         "author": info.at[title, "Book-Author"],
         "image": info.at[title, "Image-URL-M"]}
    if score is not None:
        d["score"] = round(float(score), 4)
    return d


# ---------- 1. Популярные (взвешенный рейтинг IMDb) ----------
g = df.groupby("Book-Title")["Book-Rating"].agg(["mean", "count"])
m, c = g["count"].quantile(0.90), g["mean"].mean()
g["score"] = (g["count"] * g["mean"] + m * c) / (g["count"] + m)
popular = g.sort_values("score", ascending=False)

# ---------- 2. Коллаборативная фильтрация ----------
knn_users = NearestNeighbors(metric="cosine", algorithm="brute").fit(C)
knn_books = NearestNeighbors(metric="cosine", algorithm="brute").fit(CT)

# ---------- 3. По контенту (TF-IDF: название + автор + издатель) ----------
cb = books.set_index("Book-Title").loc[titles_idx]
text = pd.Series(titles_idx, index=titles_idx) + " " + cb["Book-Author"].fillna("") + " " + cb["Publisher"].fillna("")
tfidf = TfidfVectorizer(stop_words="english").fit_transform(text.values)
knn_content = NearestNeighbors(metric="cosine", algorithm="brute").fit(tfidf)


def check_book(title):
    if title not in titles_idx:
        raise HTTPException(404, f"Книга '{title}' не найдена в модели")
    return titles_idx.get_loc(title)


def check_user(user_id):
    if user_id not in users_idx:
        raise HTTPException(404, f"Пользователь {user_id} не найден в модели")
    return users_idx.get_loc(user_id)


# ---------- Эндпоинты ----------
@app.get("/popular", summary="Топ-10 популярных книг")
def get_popular():
    return [book_json(t, r.score) for t, r in popular.head(10).iterrows()]


@app.get("/collab/users/{user_id}", summary="Коллаборативная фильтрация по пользователям")
def collab_users(user_id: int):
    u = check_user(user_id)
    dist, idx = knn_users.kneighbors(C[u], n_neighbors=21)
    w = 1 / (dist[0][1:] + 1e-9)
    scores = np.asarray(M[idx[0][1:]].T.dot(w)).ravel() / w.sum()
    scores[M[u].indices] = -1  # убираем уже оценённые
    top = np.argsort(scores)[::-1][:10]
    return [book_json(titles_idx[i], scores[i]) for i in top]


@app.get("/collab/books", summary="Коллаборативная фильтрация по книгам (похожие книги)")
def collab_books(title: str = Query(..., examples=["The Hobbit"])):
    i = check_book(title)
    dist, idx = knn_books.kneighbors(CT[i], n_neighbors=11)
    return [book_json(titles_idx[j], 1 - d) for d, j in zip(dist[0][1:], idx[0][1:])]


@app.get("/content", summary="Рекомендации по контенту (название, автор, издатель)")
def content(title: str = Query(..., examples=["The Hobbit"])):
    i = check_book(title)
    dist, idx = knn_content.kneighbors(tfidf[i], n_neighbors=11)
    return [book_json(titles_idx[j], 1 - d) for d, j in zip(dist[0][1:], idx[0][1:])]


@app.get("/personal/{user_id}", summary="Персональные рекомендации для пользователя")
def personal(user_id: int):
    """Книги, похожие на те, что пользователь оценил (с учётом его оценок).
    Если пользователя нет в модели - возвращаем топ популярных."""
    if user_id not in users_idx:
        return get_popular()
    u = users_idx.get_loc(user_id)
    rated, vals = M[u].indices, M[u].data
    dist, idx = knn_books.kneighbors(CT[rated], n_neighbors=21)
    scores = np.zeros(len(titles_idx))
    for r, d, ix in zip(vals, dist, idx):
        scores[ix[1:]] += r * (1 - d[1:])
    scores[rated] = -1
    top = np.argsort(scores)[::-1][:10]
    return [book_json(titles_idx[i], scores[i]) for i in top]
