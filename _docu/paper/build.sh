#!/bin/bash

# Build script for the paper (acmart uses bibtex, not biber)
echo "Building paper..."

pdflatex -interaction=nonstopmode paper.tex
bibtex paper
pdflatex -interaction=nonstopmode paper.tex
pdflatex -interaction=nonstopmode paper.tex

echo "Build completed!"
