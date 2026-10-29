/*
 * Dev-only S3 request signer for Swagger UI.
 *
 * The Swagger UI image cannot sign AWS requests by itself. This browser script
 * signs S3 calls with the local stand credentials before Swagger UI sends them.
 *
 * POST /dev/s3-signing/login turns signing on and optionally replaces the
 * credentials; POST /dev/s3-signing/logout turns it off. These are Swagger UI
 * controls, not real S3 API calls: state lives in localStorage of this browser.
 *
 * Credentials are intentionally hard-coded: this is a local SAIO stand, not
 * production. Do not use this approach with real AWS keys.
 */
(() => {
  const DEFAULTS = { accessKey: "test:tester", secretKey: "testing" };
  const STORAGE = {
    enabled: "swagger-s3-signing.enabled",
    accessKey: "swagger-s3-signing.accessKey",
    secretKey: "swagger-s3-signing.secretKey",
  };
  const REGION = "us-east-1";
  const SERVICE = "s3";
  const encoder = new TextEncoder();

  const readStorage = (key) => {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  };

  const writeStorage = (key, value) => {
    try {
      if (value == null) window.localStorage.removeItem(key);
      else window.localStorage.setItem(key, value);
    } catch {
      // localStorage can be unavailable; signing still works with defaults.
    }
  };

  const readCredentials = () => ({
    accessKey: readStorage(STORAGE.accessKey) || DEFAULTS.accessKey,
    secretKey: readStorage(STORAGE.secretKey) || DEFAULTS.secretKey,
  });

  const signingEnabled = () => readStorage(STORAGE.enabled) !== "off";

  const parseJsonBody = (body) => {
    if (body && typeof body === "object" && !(body instanceof Blob)) return body;
    try {
      return JSON.parse(String(body || "{}"));
    } catch {
      return {};
    }
  };

  // These two paths are Swagger UI controls, not S3 API calls. They update
  // localStorage in the browser; the nginx endpoints only return a JSON status.
  const handleControlRequest = (url, request) => {
    if (String(request.method || "").toUpperCase() !== "POST") return null;
    if (url.pathname === "/dev/s3-signing/login") {
      const body = parseJsonBody(request.body);
      const accessKey = String(body.accessKey || "").trim() || DEFAULTS.accessKey;
      const secretKey = String(body.secretKey || "") || DEFAULTS.secretKey;
      writeStorage(STORAGE.accessKey, accessKey);
      writeStorage(STORAGE.secretKey, secretKey);
      writeStorage(STORAGE.enabled, "on");
      return request;
    }
    if (url.pathname === "/dev/s3-signing/logout") {
      writeStorage(STORAGE.enabled, "off");
      writeStorage(STORAGE.accessKey, null);
      writeStorage(STORAGE.secretKey, null);
      return request;
    }
    return null;
  };

  const toHex = (bytes) =>
    Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

  const awsEncode = (value) =>
    encodeURIComponent(value).replace(/[!'()*]/g, (c) =>
      "%" + c.charCodeAt(0).toString(16).toUpperCase()
    );

  const safeDecode = (value) => {
    try {
      return decodeURIComponent(value);
    } catch {
      return value;
    }
  };

  const bodyBytes = async (body) => {
    if (body == null) return new Uint8Array(0);
    if (body instanceof Blob) return new Uint8Array(await body.arrayBuffer());
    if (body instanceof ArrayBuffer) return new Uint8Array(body);
    if (ArrayBuffer.isView(body))
      return new Uint8Array(body.buffer, body.byteOffset, body.byteLength);
    if (body instanceof URLSearchParams) return encoder.encode(body.toString());
    return encoder.encode(String(body));
  };

  const sha256 = async (bytes) =>
    new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));

  const hmac = async (key, data) => {
    const cryptoKey = await crypto.subtle.importKey(
      "raw",
      key,
      { name: "HMAC", hash: "SHA-256" },
      false,
      ["sign"]
    );
    return new Uint8Array(await crypto.subtle.sign("HMAC", cryptoKey, data));
  };

  const normalizeHeader = (value) =>
    String(value).trim().replace(/\s+/g, " ");

  const isS3Request = (pathname) => {
    if (pathname === "/api" || pathname === "/api/") return true;
    if (!pathname.startsWith("/api/")) return false;
    if (pathname.startsWith("/api/v1/")) return false;
    if (pathname.startsWith("/api/auth/")) return false;
    if (pathname === "/api/healthcheck") return false;
    if (pathname === "/api/info") return false;
    return true;
  };

  const signRequest = async (request) => {
    const url = new URL(request.url, window.location.origin);
    const control = handleControlRequest(url, request);
    if (control) return control;
    if (!isS3Request(url.pathname)) return request;
    if (!signingEnabled()) return request;

    const { accessKey, secretKey } = readCredentials();

    const headers = {};
    const sourceHeaders = request.headers || {};
    if (typeof sourceHeaders.forEach === "function" && !Array.isArray(sourceHeaders)) {
      sourceHeaders.forEach((value, name) => {
        headers[String(name).toLowerCase()] = String(value);
      });
    } else {
      Object.entries(sourceHeaders).forEach(([name, value]) => {
        headers[String(name).toLowerCase()] = String(value);
      });
    }

    delete headers.authorization;

    const payloadHash = toHex(await sha256(await bodyBytes(request.body)));
    const now = new Date();
    const amzDate = now.toISOString().replace(/[:-]|\.\d{3}/g, "");
    const dateStamp = amzDate.slice(0, 8);

    headers["host"] = `${window.location.hostname}:8080`;
    headers["x-amz-date"] = amzDate;
    headers["x-amz-content-sha256"] = payloadHash;

    const rawPath = url.pathname.replace(/^\/api/, "") || "/";
    const canonicalUri =
      rawPath
        .split("/")
        .map((segment) => awsEncode(safeDecode(segment)))
        .join("/") || "/";

    const queryPairs = [];
    url.searchParams.forEach((value, name) => {
      queryPairs.push([awsEncode(name), awsEncode(value)]);
    });
    queryPairs.sort((a, b) =>
      a[0] === b[0] ? (a[1] < b[1] ? -1 : 1) : a[0] < b[0] ? -1 : 1
    );
    const canonicalQuery = queryPairs
      .map(([name, value]) => `${name}=${value}`)
      .join("&");

    const signable = {};
    Object.entries(headers).forEach(([name, value]) => {
      if (name === "host" || name === "content-type" || name.startsWith("x-amz-")) {
        signable[name] = normalizeHeader(value);
      }
    });

    const signedHeaderNames = Object.keys(signable).sort();
    // SigV4 requires the canonical headers block to end with a newline.
    const canonicalHeaders =
      signedHeaderNames
        .map((name) => `${name}:${signable[name]}`)
        .join("\n") + "\n";
    const signedHeaders = signedHeaderNames.join(";");

    const canonicalRequest = [
      String(request.method || "GET").toUpperCase(),
      canonicalUri,
      canonicalQuery,
      canonicalHeaders,
      signedHeaders,
      payloadHash,
    ].join("\n");

    const scope = `${dateStamp}/${REGION}/${SERVICE}/aws4_request`;
    const stringToSign = [
      "AWS4-HMAC-SHA256",
      amzDate,
      scope,
      toHex(await sha256(encoder.encode(canonicalRequest))),
    ].join("\n");

    const kDate = await hmac(encoder.encode("AWS4" + secretKey), encoder.encode(dateStamp));
    const kRegion = await hmac(kDate, encoder.encode(REGION));
    const kService = await hmac(kRegion, encoder.encode(SERVICE));
    const kSigning = await hmac(kService, encoder.encode("aws4_request"));
    const signature = toHex(await hmac(kSigning, encoder.encode(stringToSign)));

    headers["authorization"] =
      `AWS4-HMAC-SHA256 Credential=${accessKey}/${scope}, ` +
      `SignedHeaders=${signedHeaders}, Signature=${signature}`;

    request.headers = {
      ...sourceHeaders,
      Authorization: headers["authorization"],
      "X-Amz-Date": amzDate,
      "X-Amz-Content-Sha256": payloadHash,
    };

    return request;
  };

  window.__s3SignRequest = signRequest;
  window.__s3SigningStatus = () => {
    const enabled = signingEnabled();
    return {
      enabled,
      accessKey: enabled ? readCredentials().accessKey : null,
    };
  };

  const Original = window.SwaggerUIBundle;
  if (typeof Original === "function") {
    const Patched = function (config) {
      const previous = config.requestInterceptor;
      config.requestInterceptor = async (request) => {
        const prepared = previous ? await previous(request) : request;
        return signRequest(prepared);
      };
      return Original(config);
    };
    Object.keys(Original).forEach((key) => {
      Patched[key] = Original[key];
    });
    window.SwaggerUIBundle = Patched;
  }
})();
