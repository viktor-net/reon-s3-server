"""Shared helpers for the S3 end-to-end tests."""

import os
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

import boto3
from botocore.client import Config
from botocore.exceptions import ClientError

ENDPOINT = os.environ.get("S3_ENDPOINT", "http://127.0.0.1:8080")
ACCESS_KEY = os.environ.get("S3_ACCESS_KEY", "test:tester")
SECRET_KEY = os.environ.get("S3_SECRET_KEY", "testing")
REGION = os.environ.get("S3_REGION", "us-east-1")

BODY = b"hello from real OpenStack Swift\n"
KEY = "dir/hello.txt"


def make_client(access_key=ACCESS_KEY, secret_key=SECRET_KEY):
    """Create a boto3 S3 client configured like the stand expects."""
    return boto3.client(
        "s3",
        endpoint_url=ENDPOINT,
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        region_name=REGION,
        config=Config(
            signature_version="s3v4",
            s3={"addressing_style": "path"},
            retries={"max_attempts": 3, "mode": "standard"},
        ),
    )


def unique_bucket(prefix="swift-s3-test-"):
    return prefix + uuid.uuid4().hex[:12]


def raw_request(url, method="GET", data=None, headers=None, timeout=30):
    """Perform an unsigned raw HTTP request and return (status, headers, body)."""
    request = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers=headers or {},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, dict(exc.headers), exc.read()


def header_value(headers, name):
    """Case-insensitive header lookup for dicts returned by raw_request."""
    wanted = name.lower()
    for key, value in headers.items():
        if key.lower() == wanted:
            return value
    return ""


def xml_error_code(body):
    """Extract <Code> from an S3 XML error body, or None for HTML/plain text."""
    try:
        root = ET.fromstring(body)
    except (ET.ParseError, ValueError):
        return None
    return root.findtext("Code")


def assert_s3_error(testcase, exc, status, code=None):
    """Assert a botocore ClientError carries the expected HTTP status and code."""
    response = exc.response
    actual_status = response.get("ResponseMetadata", {}).get("HTTPStatusCode")
    testcase.assertEqual(actual_status, status, f"unexpected HTTP status: {response}")
    if code is not None:
        actual_code = response.get("Error", {}).get("Code")
        testcase.assertEqual(actual_code, code, f"unexpected S3 error: {response}")


def abort_multipart(s3, bucket, key, upload_id):
    """Abort a multipart upload if it still exists (best effort)."""
    try:
        s3.abort_multipart_upload(Bucket=bucket, Key=key, UploadId=upload_id)
    except ClientError:
        pass


def cleanup_bucket(s3, bucket):
    """Best-effort cleanup: abort multipart uploads, delete objects, delete bucket."""
    try:
        for upload in s3.list_multipart_uploads(Bucket=bucket).get("Uploads", []):
            s3.abort_multipart_upload(
                Bucket=bucket,
                Key=upload["Key"],
                UploadId=upload["UploadId"],
            )
    except ClientError:
        pass

    try:
        for obj in s3.list_objects_v2(Bucket=bucket).get("Contents", []):
            s3.delete_object(Bucket=bucket, Key=obj["Key"])
    except ClientError:
        pass

    try:
        s3.delete_bucket(Bucket=bucket)
    except ClientError:
        pass
