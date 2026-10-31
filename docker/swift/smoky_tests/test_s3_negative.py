"""Negative and security regression tests for the Swift S3 API."""

import time
import unittest
import uuid

from botocore.exceptions import ClientError

from helpers import (
    BODY,
    ENDPOINT,
    KEY,
    abort_multipart,
    assert_s3_error,
    cleanup_bucket,
    header_value,
    make_client,
    raw_request,
    unique_bucket,
    xml_error_code,
)


class S3NegativeTest(unittest.TestCase):
    def setUp(self):
        self.s3 = make_client()
        self.bucket = unique_bucket()
        self.s3.create_bucket(Bucket=self.bucket)
        self.s3.put_object(Bucket=self.bucket, Key=KEY, Body=BODY)
        self.addCleanup(cleanup_bucket, self.s3, self.bucket)

    def test_unsigned_list_buckets_is_rejected(self):
        status, headers, _ = raw_request(ENDPOINT + "/")

        self.assertIn(status, (403, 404))
        # Swift answers unsigned requests with plain HTML, not an S3 XML error.
        self.assertNotIn("xml", header_value(headers, "Content-Type").lower())

    def test_unsigned_object_get_is_rejected(self):
        status, _, body = raw_request(f"{ENDPOINT}/{self.bucket}/{KEY}")

        self.assertNotEqual(status, 200)
        self.assertNotIn(BODY, body)

    def test_unsigned_object_delete_is_rejected(self):
        status, _, _ = raw_request(
            f"{ENDPOINT}/{self.bucket}/{KEY}", method="DELETE"
        )

        self.assertNotIn(status, (200, 204))
        # The object must still exist after the unsigned DELETE attempt.
        head = self.s3.head_object(Bucket=self.bucket, Key=KEY)
        self.assertEqual(head["ContentLength"], len(BODY))

    def test_bad_secret_key_returns_403(self):
        with self.assertRaises(ClientError) as ctx:
            make_client(secret_key="wrong-secret").list_buckets()

        assert_s3_error(self, ctx.exception, 403, "SignatureDoesNotMatch")

    def test_bad_access_key_returns_403(self):
        with self.assertRaises(ClientError) as ctx:
            make_client(access_key="bad:key", secret_key="bad-secret").list_buckets()

        self.assertEqual(
            ctx.exception.response["ResponseMetadata"]["HTTPStatusCode"], 403
        )
        self.assertIn(
            ctx.exception.response["Error"]["Code"],
            {"SignatureDoesNotMatch", "InvalidAccessKeyId"},
        )

    def test_missing_bucket_returns_404(self):
        with self.assertRaises(ClientError) as ctx:
            self.s3.get_object(Bucket="missing-bucket-xyz", Key=KEY)

        assert_s3_error(self, ctx.exception, 404, "NoSuchBucket")

    def test_missing_key_returns_404(self):
        with self.assertRaises(ClientError) as ctx:
            self.s3.get_object(Bucket=self.bucket, Key="missing.txt")

        assert_s3_error(self, ctx.exception, 404, "NoSuchKey")

    def test_delete_non_empty_bucket_returns_409(self):
        with self.assertRaises(ClientError) as ctx:
            self.s3.delete_bucket(Bucket=self.bucket)

        assert_s3_error(self, ctx.exception, 409, "BucketNotEmpty")

    def test_invalid_bucket_name_returns_400(self):
        invalid = "Bad_Name_" + uuid.uuid4().hex[:6]
        with self.assertRaises(ClientError) as ctx:
            self.s3.create_bucket(Bucket=invalid)

        assert_s3_error(self, ctx.exception, 400, "InvalidBucketName")

    def test_presigned_tampered_signature_returns_403(self):
        url = self.s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": self.bucket, "Key": KEY},
            ExpiresIn=300,
        )
        tampered = url[:-1] + ("0" if url[-1] != "0" else "1")

        status, _, body = raw_request(tampered)

        self.assertEqual(status, 403)
        self.assertEqual(xml_error_code(body), "SignatureDoesNotMatch")

    def test_presigned_expired_url_returns_403(self):
        url = self.s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": self.bucket, "Key": KEY},
            ExpiresIn=1,
        )
        time.sleep(2)

        status, _, body = raw_request(url)

        self.assertEqual(status, 403)
        self.assertEqual(xml_error_code(body), "AccessDenied")

    def test_presigned_url_for_other_path_returns_403(self):
        url = self.s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": self.bucket, "Key": KEY},
            ExpiresIn=300,
        )
        changed = url.replace(KEY, "dir/other.txt", 1)
        self.assertNotEqual(changed, url)

        status, _, body = raw_request(changed)

        self.assertEqual(status, 403)
        self.assertEqual(xml_error_code(body), "SignatureDoesNotMatch")

    def test_presigned_get_url_cannot_be_used_for_put(self):
        url = self.s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": self.bucket, "Key": KEY},
            ExpiresIn=300,
        )

        status, _, _ = raw_request(url, method="PUT", data=b"overwrite")

        self.assertEqual(status, 403)

    def test_multipart_with_wrong_etag_returns_400(self):
        upload_id = self.s3.create_multipart_upload(
            Bucket=self.bucket, Key="bad-part.bin"
        )["UploadId"]
        self.addCleanup(
            abort_multipart, self.s3, self.bucket, "bad-part.bin", upload_id
        )
        self.s3.upload_part(
            Bucket=self.bucket,
            Key="bad-part.bin",
            PartNumber=1,
            UploadId=upload_id,
            Body=b"x" * (5 * 1024 * 1024),
        )

        with self.assertRaises(ClientError) as ctx:
            self.s3.complete_multipart_upload(
                Bucket=self.bucket,
                Key="bad-part.bin",
                UploadId=upload_id,
                MultipartUpload={
                    "Parts": [{"ETag": "deadbeef", "PartNumber": 1}]
                },
            )

        assert_s3_error(self, ctx.exception, 400, "InvalidPart")


if __name__ == "__main__":
    unittest.main(verbosity=2)
