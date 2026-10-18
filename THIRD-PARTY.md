# Third-party parts

The module contains no third-party code. The mode timings, line layouts and VIS codes follow the published SSTV specifications (N7CXI's "Proposal for SSTV Mode Specifications", Dayton 2000, and KB4YZ's tables). The FSK ID follows MMSSTV's (JE3HHT) format. Both were checked against two open-source programs:

- **slowrx** by Oona Räisänen, OH2EIQ (https://github.com/windytan/slowrx): its mode table and how it lays out each mode's lines.
- **PySSTV** by András Veres-Szentkirályi (https://github.com/dnet/pysstv), MIT License: an independent encoder. The self-test can decode its pictures (`build.ps1 -PySstv`). It isn't shipped with the module.
