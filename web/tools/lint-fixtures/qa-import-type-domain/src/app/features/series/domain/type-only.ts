// QA guard: a type-only import of a banned module is still reported.
import type { Component } from '@angular/core';

export type Comp = Component;
