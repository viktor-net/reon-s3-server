# S3 smoke and security tests

Набор проверок работоспособности S3 API внутри контейнера `swift`. Тесты
ходят в S3 через настоящий `boto3`, а не через браузерный signer из Swagger UI.

## Состав

```text
smoky_tests/
├── README.md               # этот файл
├── helpers.py              # настройки, boto3-клиент, cleanup
├── test_s3_positive.py     # успешные сценарии
├── test_s3_negative.py     # негативные и security-проверки
└── run.py                  # запуск всего набора
```

## Запуск

Все тесты:

```bash
docker compose exec swift smoke-tests
```

Тесты лежат внутри образа. После изменения файлов в `smoky_tests/` пересоберите
контейнер:

```bash
docker compose build swift
docker compose up -d swift
```

То же самое напрямую:

```bash
docker compose exec swift python3 /opt/smoky_tests/run.py
```

Только успешные сценарии:

```bash
docker compose exec swift python3 -m unittest discover \
  -s /opt/smoky_tests -t /opt/smoky_tests \
  -p 'test_s3_positive.py' -v
```

Только негативные проверки:

```bash
docker compose exec swift python3 -m unittest discover \
  -s /opt/smoky_tests -t /opt/smoky_tests \
  -p 'test_s3_negative.py' -v
```

С хост-машины, если установлены Python и `boto3`:

```bash
export S3_ENDPOINT='http://localhost:8080'
python -m unittest discover -s smoky_tests -t smoky_tests -v
```

Или напрямую:

```bash
python smoky_tests/run.py
```

## Параметры

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `S3_ENDPOINT` | `http://127.0.0.1:8080` | адрес S3 API |
| `S3_ACCESS_KEY` | `test:tester` | access key |
| `S3_SECRET_KEY` | `testing` | secret key |
| `S3_REGION` | `us-east-1` | регион |

Пример для другого порта:

```bash
docker compose exec -e S3_ENDPOINT='http://127.0.0.1:8080' swift smoke-tests
```

## Что проверяет positive-набор

Тест создаёт временный бакет `swift-s3-test-<uuid>` и проверяет:

1. создание бакета и загрузку объекта `dir/hello.txt`;
2. `GetObject` и сравнение тела;
3. `HeadObject`: размер и метаданные;
4. `ListObjectsV2`;
5. `CopyObject`;
6. presigned GET URL без заголовка `Authorization`;
7. multipart upload из двух частей по 5 МиБ;
8. multi-object delete;
9. очистку бакета.

## Что проверяет negative-набор

| Проверка | Ожидаемый результат |
|---|---|
| `GET /` без подписи | `403`/`404`, обычный HTML, а не S3 XML |
| `GET` объекта без подписи | не `200`, тело объекта не отдаётся |
| `DELETE` объекта без подписи | не `200`/`204`, объект остаётся на месте |
| неверный secret key | `403 SignatureDoesNotMatch` |
| неверный access key | `403` |
| несуществующий бакет | `404 NoSuchBucket` |
| несуществующий ключ | `404 NoSuchKey` |
| удаление непустого бакета | `409 BucketNotEmpty` |
| некорректное имя бакета | `400 InvalidBucketName` |
| испорченная подпись presigned URL | `403 SignatureDoesNotMatch` |
| просроченный presigned URL | `403 AccessDenied` |
| presigned URL для другого пути | `403 SignatureDoesNotMatch` |
| presigned GET, использованный как PUT | `403` |
| multipart с неверным ETag | `400 InvalidPart` |

Негативные проверки ловят регрессии в авторизации: случайно открытый анонимный
доступ, отключённую проверку подписи, неверную обработку presigned URL.

## Когда запускать

- после изменения `etc/swift/*` или `Dockerfile`;
- после пересборки или перезапуска контейнера;
- когда нужно быстро проверить, что стенд жив.

## Если тест не прошёл

```bash
docker compose ps
curl -i http://localhost:8080/healthcheck
docker compose logs --tail=200 swift
```

Типичные причины:

- контейнер ещё не перешёл в `healthy`;
- занят или изменён порт `8080`;
- изменены ключи в `etc/swift/proxy-server.conf`, а переменные окружения
  остались старыми;
- сломана конфигурация pipeline или rings;
- не хватает места в volume `swift-data`.

## Ограничения

Это smoke/security-набор, а не нагрузочный тест и не проверка всех
возможностей S3. Он не покрывает ACL, версионирование, CORS, bucket policy,
lifecycle, object lock и тонкие настройки multipart.
