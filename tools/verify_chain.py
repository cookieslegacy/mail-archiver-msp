#!/usr/bin/env python3
"""
Independent verification of a Mail-Archiver originals chain.

Recomputes every link of the CSV that "Compliance > Export chain (CSV)" produces, without
the application or its database, and optionally checks original .eml files against the
recorded SHA-256 and an anchor document against the chain.

    python3 verify_chain.py originals-chain.csv
    python3 verify_chain.py originals-chain.csv --emails ./downloaded-originals
    python3 verify_chain.py originals-chain.csv --anchor anchor.json

Canonical form of a link (see Migrations/*_MigrateFork_2.cs, ma_original_chain_input):

    "MA-ORIGINAL-v1|<organization_id>|<chain_seq>|<prev_chain_hash>|<archived_email_id>|
     <mail_account_id>|<source>|<content_sha256>|<size_bytes>|<archived_at_epoch_us>|<message_id>"

    chain_hash = lowercase hex SHA-256 of that text (UTF-8); the first link's
    prev_chain_hash is 64 zeros.

Only the Python standard library is used. Exit code 0 = everything verified.
"""
import argparse
import csv
import hashlib
import json
import os
import sys

GENESIS = "0" * 64


def chain_input(row):
    return "|".join([
        "MA-ORIGINAL-v1",
        row["organization_id"],
        row["chain_seq"],
        row["prev_chain_hash"],
        row["archived_email_id"],
        row["mail_account_id"],
        row["source"],
        row["content_sha256"],
        row["size_bytes"],
        row["archived_at_epoch_us"],
        row["message_id"],
    ])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("csv_file")
    parser.add_argument("--emails", help="directory with original-<emailId>-*.eml files to check against the chain")
    parser.add_argument("--anchor", help="anchor JSON document (from the WORM store) to check against the chain")
    args = parser.parse_args()

    with open(args.csv_file, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f, delimiter=";"))

    problems = 0
    expected_seq = 1
    previous = GENESIS
    by_email = {}
    by_seq = {}
    for row in rows:
        seq = int(row["chain_seq"])
        if seq != expected_seq:
            print(f"#{seq}: sequence gap, expected #{expected_seq}")
            problems += 1
        if row["prev_chain_hash"] != previous:
            print(f"#{seq}: link to the previous entry is broken")
            problems += 1
        computed = hashlib.sha256(chain_input(row).encode("utf-8")).hexdigest()
        if computed != row["chain_hash"]:
            print(f"#{seq}: chain hash does not match the entry (email {row['archived_email_id']})")
            problems += 1
        expected_seq = seq + 1
        previous = row["chain_hash"]
        by_email[row["archived_email_id"]] = row
        by_seq[seq] = row

    print(f"{len(rows)} chain entries checked, head #{expected_seq - 1} {previous}")

    if args.emails:
        checked = 0
        for name in sorted(os.listdir(args.emails)):
            if not (name.startswith("original-") and name.endswith(".eml")):
                continue
            email_id = name.split("-")[1]
            row = by_email.get(email_id)
            if row is None:
                print(f"{name}: email {email_id} is not in the chain")
                problems += 1
                continue
            with open(os.path.join(args.emails, name), "rb") as f:
                data = f.read()
            if hashlib.sha256(data).hexdigest() != row["content_sha256"] or len(data) != int(row["size_bytes"]):
                print(f"{name}: does not match the SHA-256 recorded in chain entry #{row['chain_seq']}")
                problems += 1
            checked += 1
        print(f"{checked} original files checked")

    if args.anchor:
        with open(args.anchor, encoding="utf-8") as f:
            anchor = json.load(f)
        if anchor.get("chain") == "originals":
            row = by_seq.get(int(anchor["seq"]))
            if row is None or row["chain_hash"] != anchor["hash"]:
                print(f"anchor #{anchor['seq']} of {anchor['createdAt']}: the chain no longer matches it")
                problems += 1
            else:
                print(f"anchor #{anchor['seq']} of {anchor['createdAt']} matches the chain")

    if problems:
        print(f"{problems} PROBLEM(S) FOUND")
        sys.exit(1)
    print("OK: chain verified")


if __name__ == "__main__":
    main()
