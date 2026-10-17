import { ChangeDetectionStrategy, Component, DestroyRef, ViewContainerRef, computed, inject, input, output, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom, merge } from 'rxjs';
import { ConfirmDialog, ErrorState, LoadingState, StatusChip } from '@shared/ui';
import { DiffViewComponent } from '@shared/ui/diff-view/diff-view.component';
import type { DiffRow } from '@shared/ui/diff-view/diff-row';
import type { BibleProposal } from '../domain/bible.model';

@Component({
  selector: 'nts-bible-proposal-banner',
  imports: [MatButtonModule, TranslocoPipe, ErrorState, LoadingState, StatusChip, DiffViewComponent],
  templateUrl: './bible-proposal-banner.html', styleUrl: './bible-proposal-banner.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BibleProposalBanner {
  readonly proposal = input.required<BibleProposal>();
  readonly version = input.required<number>();
  readonly proposedTitle = input<string | null>(null);
  readonly pending = input(false);
  readonly accept = output<void>();
  readonly dismiss = output<void>();
  private readonly translator = inject(TranslocoService);
  private readonly translations = toSignal(merge(this.translator.langChanges$, this.translator.events$), { initialValue: null });
  private readonly dialog = inject(ConfirmDialog);
  private readonly viewContainer = inject(ViewContainerRef);
  private readonly destroy = inject(DestroyRef);
  private readonly askingDismiss = signal(false);
  readonly heading = computed(() => {
    this.translations();
    const title = this.proposedTitle();
    if (!title) return this.translator.translate('bible.proposal.bannerNoTitle', { n: this.proposal().ops.length }, 'series');
    // Substitute untrusted titles after translation: a literal {{title}} inside an episode title must not
    // re-enter Transloco's recursive parameter interpolation.
    const marker = '\uFFFC';
    return this.translator.translate<string>('bible.proposal.banner', { n: this.proposal().ops.length, title: marker }, 'series').split(marker).join(title);
  });
  readonly rows = computed<readonly DiffRow[]>(() => {
    this.translations();
    return this.proposal().ops.map(op => {
      const normalised = op.kind.replace(/^add_?/i, '').replace(/[^a-z]/gi, '').toLowerCase();
      const labelKey = normalised === 'summary' ? 'bible.op.summary' : normalised === 'character' ? 'bible.op.character' : normalised === 'place' ? 'bible.op.place' : normalised === 'rule' ? 'bible.op.rule' : 'bible.op.other';
      return {
        kind: /^add_?/i.test(op.kind) ? 'add' : 'change',
        label: this.translator.translate(labelKey, {}, 'series'),
        after: op.name ?? op.text ?? op.summary ?? op.description ?? '',
        ...(op.reason ? { reason: `${this.translator.translate('bible.proposal.more', {}, 'series')}: ${op.reason}` } : {}),
      };
    });
  });
  async confirmDismiss(): Promise<void> {
    if (this.pending() || this.askingDismiss()) return;
    const proposal = this.proposal();
    this.askingDismiss.set(true);
    try {
      const ref = this.dialog.confirm({ titleKey: 'series.bible.proposal.dismissTitle', bodyKey: 'series.bible.proposal.dismissBody', confirmLabelKey: 'series.bible.proposal.dismiss', destructive: true }, { viewContainerRef: this.viewContainer });
      const confirmed = await firstValueFrom(ref.afterClosed());
      if (confirmed && !this.destroy.destroyed && proposal === this.proposal() && !this.pending()) this.dismiss.emit();
    } finally { if (!this.destroy.destroyed) this.askingDismiss.set(false); }
  }
}
