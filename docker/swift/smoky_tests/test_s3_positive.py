"""Positive end-to-end tests for the Swift S3 API."""

import unittest
import urllib.request

from helpers import BODY, KEY, abort_multipart, cleanup_bucket, make_client, unique_bucket


class S3PositiveTest(unittest.TestCase):
    def setUp(self):
        self.s3 = make_client()
        self.bucket = unique_bucket()
        self.s3.create_bucket(Bucket=self.bucket)
        self.addCleanup(cleanup_bucket, self.s3, self.bucket)

    def test_put_get_head(self):
        self.s3.put_object(
            Bucket=self.bucket,
            Key=KEY,
            Body=BODY,
            ContentType="text/plain",
            Metadata={"source": "smoke-test"},
        )

        body = self.s3.get_object(Bucket=self.bucket, Key=KEY)["Body"].read()
        self.assertEqual(body, BODY)

        head = self.s3.head_object(Bucket=self.bucket, Key=KEY)
        self.assertEqual(head["ContentLength"], len(BODY))
        self.assertEqual(head["Metadata"]["source"], "smoke-test")

    def test_list_objects_v2(self):
        self.s3.put_object(Bucket=self.bucket, Key=KEY, Body=BODY)

        listing = self.s3.list_objects_v2(Bucket=self.bucket)
        self.assertEqual([obj["Key"] for obj in listing.get("Contents", [])], [KEY])

    def test_copy_object(self):
        self.s3.put_object(Bucket=self.bucket, Key=KEY, Body=BODY)

        self.s3.copy_object(
            Bucket=self.bucket,
            Key="dir/copy.txt",
            CopySource={"Bucket": self.bucket, "Key": KEY},
        )

        body = self.s3.get_object(Bucket=self.bucket, Key="dir/copy.txt")["Body"].read()
        self.assertEqual(body, BODY)

    def test_presigned_get(self):
        self.s3.put_object(Bucket=self.bucket, Key=KEY, Body=BODY)
        url = self.s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": self.bucket, "Key": KEY},
            ExpiresIn=300,
        )

        with urllib.request.urlopen(url, timeout=30) as response:
            self.assertEqual(response.status, 200)
            self.assertEqual(response.read(), BODY)

    def test_multipart_upload(self):
        part_size = 5 * 1024 * 1024
        upload_id = self.s3.create_multipart_upload(
            Bucket=self.bucket, Key="big.bin"
        )["UploadId"]
        self.addCleanup(
            abort_multipart, self.s3, self.bucket, "big.bin", upload_id
        )

        parts = []
        for number in (1, 2):
            result = self.s3.upload_part(
                Bucket=self.bucket,
                Key="big.bin",
                PartNumber=number,
                UploadId=upload_id,
                Body=bytes([number]) * part_size,
            )
            parts.append({"ETag": result["ETag"], "PartNumber": number})

        self.s3.complete_multipart_upload(
            Bucket=self.bucket,
            Key="big.bin",
            UploadId=upload_id,
            MultipartUpload={"Parts": parts},
        )

        head = self.s3.head_object(Bucket=self.bucket, Key="big.bin")
        self.assertEqual(head["ContentLength"], 2 * part_size)

    def test_delete_objects(self):
        keys = ["dir/hello.txt", "dir/copy.txt", "big.bin"]
        for key in keys:
            self.s3.put_object(Bucket=self.bucket, Key=key, Body=BODY)

        response = self.s3.delete_objects(
            Bucket=self.bucket,
            Delete={"Objects": [{"Key": key} for key in keys]},
        )
        self.assertEqual(len(response.get("Deleted", [])), len(keys))

        listing = self.s3.list_objects_v2(Bucket=self.bucket)
        self.assertEqual(listing.get("Contents", []), [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
