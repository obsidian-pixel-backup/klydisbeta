# Klydis 10k+ JSONL Training Dataset

This directory contains the generator and a small validation suite for creating a 10,000+ example JSONL training dataset for Klydis. Use the generator script to produce the full klydis_training_10000.jsonl file in the repository.

Files:
- generate_klydis_dataset.py — generator script that creates klydis_training_10000.jsonl and a 200-example validation suite.
- validation_suite_200.jsonl — 200 curated behavioral validation prompts (sample).  
- METADATA.json — metadata about the dataset that will be updated after generation.

Usage:

1. Run the generator locally (requires Python 3.8+):

   python generate_klydis_dataset.py --output datasets/klydis_training_10000.jsonl --count 10000

2. The script writes the JSONL file and prints a SHA256 checksum.

Notes:
- The generated examples follow the schema described in this repo's README and are intended for supervised fine-tuning of an LLM to operate the Klydis harness. Make sure you review and possibly filter or augment examples to match your training objectives.
